using System.Globalization;
using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Application.Tests.Execution;

public sealed class ExecutionManifestCompilerTests
{
    [Fact]
    public void CompilesOnlyReadyItemsInDeterministicSourceOrder()
    {
        ManifestTestPlan.TestOperation[] operations =
        [
            Move(@"C:\Inbox\z.zip", @"C:\Sorted\Archives", "archives"),
            Move(@"C:\Inbox\a.txt", @"C:\Sorted\Text", "text"),
        ];

        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(
            ManifestTestPlan.Create(operations.Reverse()));

        Assert.True(compilation.Succeeded);
        Assert.Empty(compilation.Problems);
        ExecutionManifest manifest = Assert.IsType<ExecutionManifest>(compilation.Manifest);
        Assert.Equal(ManifestTestPlan.GeneratedAt, manifest.GeneratedAtUtc);
        Assert.Equal(@"C:\Inbox", manifest.SourceRoot.Value);
        Assert.Equal(ManifestTestPlan.Policy.Semantics, manifest.PathSemantics);
        Assert.Collection(
            manifest.Items,
            item =>
            {
                Assert.Equal(0, item.Ordinal);
                Assert.Equal(@"C:\Inbox\a.txt", item.SourcePrecondition.CanonicalPath.Value);
                Assert.Equal(@"C:\Sorted\Text", item.DestinationDirectory.Value);
                Assert.Equal(@"C:\Sorted\Text\a.txt", item.TargetPath.Value);
                Assert.Equal("text", item.SelectedRuleName);
                Assert.Equal(OrganizationActionKind.MoveToDirectory, item.ActionKind);
            },
            item =>
            {
                Assert.Equal(1, item.Ordinal);
                Assert.Equal(@"C:\Inbox\z.zip", item.SourcePrecondition.CanonicalPath.Value);
                Assert.Equal(@"C:\Sorted\Archives\z.zip", item.TargetPath.Value);
            });
    }

    [Fact]
    public void ExcludesSkippedItemsFromTheManifest()
    {
        ManifestTestPlan.TestOperation operation =
            Move(@"C:\Inbox\ready.txt", @"C:\Sorted", "text");
        ExecutionManifest manifest = Compile(
            ManifestTestPlan.Create(
                [operation],
                unmatchedSourcePaths: [@"C:\Inbox\ignored.zip"]));

        Assert.Single(manifest.Items);
        Assert.Equal(@"C:\Inbox\ready.txt", manifest.Items[0].SourcePrecondition.CanonicalPath.Value);
    }

    [Fact]
    public void RejectsAnIncompletePlanBeforeProducingAManifest()
    {
        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(
            ManifestTestPlan.Create(
                [Move(@"C:\Inbox\a.txt", @"C:\Sorted", "move")],
                [new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, @"C:\Inbox\private")]
            ));

        Assert.False(compilation.Succeeded);
        Assert.Null(compilation.Manifest);
        ExecutionManifestProblem problem = Assert.Single(compilation.Problems);
        Assert.Equal(ExecutionManifestProblemKind.PlanNotEligible, problem.Kind);
    }

    [Fact]
    public void RejectsTheWholePlanWhenAnyReadyActionIsNotMove()
    {
        ManifestTestPlan.TestOperation move =
            Move(@"C:\Inbox\a.txt", @"C:\Sorted\Text", "move");
        ManifestTestPlan.TestOperation copy = new(
            @"C:\Inbox\b.zip",
            new CopyToDirectoryAction(@"C:\Sorted\Archives"),
            "copy");

        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(
            ManifestTestPlan.Create([move, copy]));

        Assert.False(compilation.Succeeded);
        Assert.Null(compilation.Manifest);
        ExecutionManifestProblem problem = Assert.Single(compilation.Problems);
        Assert.Equal(ExecutionManifestProblemKind.UnsupportedReadyAction, problem.Kind);
        Assert.Equal(OrganizationActionKind.CopyToDirectory, problem.ActionKind);
        Assert.Equal(@"C:\Inbox\b.zip", problem.AffectedSource?.Value);
    }

    [Fact]
    public void FingerprintIsIndependentOfInputOrderCulturePlanTimeAndAuditRuleName()
    {
        ManifestTestPlan.TestOperation[] forward =
        [
            Move(@"C:\Inbox\I.txt", @"C:\Sorted\Text", "first name"),
            Move(@"C:\Inbox\z.zip", @"C:\Sorted\Archives", "second name"),
        ];
        ManifestTestPlan.TestOperation[] reversedWithDifferentAuditNames =
        [
            Move(@"C:\Inbox\z.zip", @"C:\Sorted\Archives", "renamed archive rule"),
            Move(@"C:\Inbox\I.txt", @"C:\Sorted\Text", "renamed text rule"),
        ];

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            PlanFingerprint first = Compile(ManifestTestPlan.Create(forward)).Fingerprint;

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            PlanFingerprint second = Compile(
                ManifestTestPlan.Create(
                    reversedWithDifferentAuditNames,
                    generatedAt: ManifestTestPlan.GeneratedAt.AddYears(1))).Fingerprint;

            Assert.Equal(first, second);
            Assert.Equal(first.Value, second.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void FingerprintChangesWhenExecutionCriticalSourceMetadataChanges()
    {
        ExecutionManifest first = Compile(
            ManifestTestPlan.Create(
                [Move(@"C:\Inbox\a.txt", @"C:\Sorted", "move", length: 10)]));
        ExecutionManifest second = Compile(
            ManifestTestPlan.Create(
                [Move(@"C:\Inbox\a.txt", @"C:\Sorted", "move", length: 11)]));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void FingerprintUsesLogicalPathIdentityAcrossEquivalentCaseAndUnicodeSpellings()
    {
        const string composed = "caf\u00e9";
        const string decomposed = "cafe\u0301";
        PathPolicy policy = new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);

        ExecutionManifest first = Compile(
            ManifestTestPlan.Create(
                [
                    new ManifestTestPlan.TestOperation(
                        $@"C:\Inbox\{composed}.TXT",
                        new MoveToDirectoryAction($@"C:\Sorted\{composed}"),
                        "first audit name"),
                ],
                pathPolicy: policy,
                sourceRoot: @"C:\Inbox"));
        ExecutionManifest second = Compile(
            ManifestTestPlan.Create(
                [
                    new ManifestTestPlan.TestOperation(
                        $@"c:\inbox\{decomposed}.txt",
                        new MoveToDirectoryAction($@"c:\sorted\{decomposed}"),
                        "second audit name"),
                ],
                pathPolicy: policy,
                sourceRoot: @"c:\inbox"));

        Assert.NotEqual(first.SourceRoot.Value, second.SourceRoot.Value);
        Assert.NotEqual(
            first.Items[0].SourcePrecondition.CanonicalPath.Value,
            second.Items[0].SourcePrecondition.CanonicalPath.Value);
        Assert.True(policy.AreEquivalent(first.SourceRoot, second.SourceRoot));
        Assert.True(
            policy.AreEquivalent(
                first.Items[0].TargetPath,
                second.Items[0].TargetPath));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void FingerprintHandlesEveryUtf16CodeUnitAcceptedWithoutUnicodeNormalization()
    {
        const string unpairedHighSurrogate = "\ud800";
        PathPolicy policy = new(
            PathSyntax.Posix,
            PathCaseSensitivity.Sensitive,
            PathUnicodeNormalization.None);
        ManifestTestPlan.TestOperation operation = new(
            "/inbox/report-" + unpairedHighSurrogate + ".txt",
            new MoveToDirectoryAction("/sorted"),
            "move");

        ExecutionManifest first = Compile(
            ManifestTestPlan.Create(
                [operation],
                pathPolicy: policy,
                sourceRoot: "/inbox"));
        ExecutionManifest second = Compile(
            ManifestTestPlan.Create(
                [operation],
                pathPolicy: policy,
                sourceRoot: "/inbox"));

        Assert.Contains(
            unpairedHighSurrogate,
            first.Items[0].SourcePrecondition.CanonicalPath.Value,
            StringComparison.Ordinal);
        Assert.Equal(PlanFingerprint.HexLength, first.Fingerprint.Value.Length);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void ManifestAndCompilationCollectionsCannotBeMutatedThroughTheirPublicViews()
    {
        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(
            ManifestTestPlan.Create(
                [Move(@"C:\Inbox\a.txt", @"C:\Sorted", "move")]));
        ExecutionManifest manifest = Compile(compilation);

        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionManifestItem>)manifest.Items).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionManifestProblem>)compilation.Problems).Clear());
    }

    private static ManifestTestPlan.TestOperation Move(
        string source,
        string destination,
        string ruleName,
        long length = 10) =>
        new(source, new MoveToDirectoryAction(destination), ruleName, length);

    private static ExecutionManifest Compile(FileOrganizer.Core.Planning.OrganizationPlan plan) =>
        Compile(ExecutionManifestCompiler.Compile(plan));

    private static ExecutionManifest Compile(ExecutionManifestCompilation compilation) =>
        Assert.IsType<ExecutionManifest>(compilation.Manifest);

}
