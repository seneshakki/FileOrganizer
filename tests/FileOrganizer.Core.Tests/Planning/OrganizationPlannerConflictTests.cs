using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;
using FileOrganizer.Core.Tests.Matching;

namespace FileOrganizer.Core.Tests.Planning;

public sealed class OrganizationPlannerConflictTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 10, 11, 12, TimeSpan.Zero);

    [Theory]
    [InlineData(DestinationEntryKind.File, PlanReasonKind.DestinationFileExists)]
    [InlineData(DestinationEntryKind.Directory, PlanReasonKind.DestinationDirectoryExists)]
    public void ExistingDestinationIsAConflict(
        DestinationEntryKind entryKind,
        PlanReasonKind expectedReason)
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(
                policy,
                DirectoryFacts(preparation).Append(
                    new DestinationPathFact(target, entryKind))));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Conflict, item.Outcome);
        Assert.Equal(expectedReason, Assert.Single(item.Reasons).Kind);
        Assert.False(plan.IsEligibleForExecution);
    }

    [Theory]
    [InlineData(DestinationEntryKind.Missing, PlanReasonKind.DestinationDirectoryMissing)]
    [InlineData(DestinationEntryKind.File, PlanReasonKind.DestinationPathIsNotDirectory)]
    public void DestinationDirectoryMustAlreadyExistAsADirectory(
        DestinationEntryKind directoryState,
        PlanReasonKind expectedReason)
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);
        CanonicalPath directory = Assert.Single(preparation.DestinationDirectories);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(
                policy,
                [
                    new DestinationPathFact(directory, directoryState),
                    new DestinationPathFact(target, DestinationEntryKind.Missing),
                ]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        PlanReason reason = Assert.Single(item.Reasons);
        Assert.Equal(expectedReason, reason.Kind);
        Assert.Equal(directory.Value, reason.AffectedPath?.Value);
        Assert.False(plan.IsEligibleForExecution);
    }

    [Fact]
    public void UnsupportedDestinationDirectoryBlocksThePlan()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);
        CanonicalPath directory = Assert.Single(preparation.DestinationDirectories);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);
        DestinationInspectionFailure failure = new(
            DestinationInspectionFailureKind.UnsupportedEntry,
            "The destination directory is a reparse point.");

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(
                policy,
                [
                    new DestinationPathFact(
                        directory,
                        DestinationEntryKind.InspectionFailed,
                        failure),
                    new DestinationPathFact(target, DestinationEntryKind.Missing),
                ]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        PlanReason reason = Assert.Single(item.Reasons);
        Assert.Equal(PlanReasonKind.DestinationInspectionFailed, reason.Kind);
        Assert.Equal(
            DestinationInspectionFailureKind.UnsupportedEntry,
            reason.DestinationInspectionFailure?.Kind);
        Assert.Equal(directory.Value, reason.AffectedPath?.Value);
    }

    [Fact]
    public void DestinationInspectionFailureBlocksInsteadOfAssumingAbsence()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);
        DestinationInspectionFailure failure = new(
            DestinationInspectionFailureKind.AccessDenied,
            "Destination access was denied.");

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(
                policy,
                DirectoryFacts(preparation).Append(
                    new DestinationPathFact(
                        target,
                        DestinationEntryKind.InspectionFailed,
                        failure))));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        PlanReason reason = Assert.Single(item.Reasons);
        Assert.Equal(PlanReasonKind.DestinationInspectionFailed, reason.Kind);
        Assert.Equal(
            DestinationInspectionFailureKind.AccessDenied,
            reason.DestinationInspectionFailure?.Kind);
        Assert.False(plan.IsEligibleForExecution);
    }

    [Fact]
    public void MissingDestinationFactBlocksInsteadOfAssumingAbsence()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(policy, DirectoryFacts(preparation)));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        Assert.Equal(PlanReasonKind.DestinationStateMissing, Assert.Single(item.Reasons).Kind);
    }

    [Fact]
    public void ConflictingEquivalentFactsBlockAsAmbiguous()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(policy);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);
        CanonicalPath equivalent = Path(policy, target.Value.ToUpperInvariant());

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(
                policy,
                DirectoryFacts(preparation).Concat(
                [
                    new DestinationPathFact(target, DestinationEntryKind.Missing),
                    new DestinationPathFact(equivalent, DestinationEntryKind.File),
                ])));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        Assert.Equal(PlanReasonKind.AmbiguousDestinationState, Assert.Single(item.Reasons).Kind);
    }

    [Fact]
    public void EveryItemInADuplicateTargetGroupConflictsRegardlessOfInputOrder()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        FileEntry first = Entry(policy, @"C:\Inbox\one\report.pdf");
        FileEntry second = Entry(policy, @"C:\Inbox\two\report.pdf");

        OrganizationPlan forward = CompleteMissing(
            Prepare(policy, [first, second], @"C:\Archive"));
        OrganizationPlan reversed = CompleteMissing(
            Prepare(policy, [second, first], @"C:\Archive"));

        Assert.All(
            forward.Items,
            item =>
            {
                Assert.Equal(PlanItemOutcome.Conflict, item.Outcome);
                Assert.Contains(
                    item.Reasons,
                    reason => reason.Kind == PlanReasonKind.DuplicateDestination);
            });
        Assert.Equal(Projection(forward), Projection(reversed));
    }

    [Fact]
    public void CaseEquivalentTargetsConflictOnlyUnderAnInsensitivePolicy()
    {
        PathPolicy insensitive = Policy(caseSensitive: false, normalizeUnicode: false);
        PathPolicy sensitive = Policy(caseSensitive: true, normalizeUnicode: false);

        OrganizationPlan insensitivePlan = CompleteMissing(
            Prepare(
                insensitive,
                [
                    Entry(insensitive, @"C:\Inbox\one\REPORT.txt"),
                    Entry(insensitive, @"C:\Inbox\two\report.txt"),
                ],
                @"C:\Archive"));
        OrganizationPlan sensitivePlan = CompleteMissing(
            Prepare(
                sensitive,
                [
                    Entry(sensitive, @"C:\Inbox\one\REPORT.txt"),
                    Entry(sensitive, @"C:\Inbox\two\report.txt"),
                ],
                @"C:\Archive"));

        Assert.All(
            insensitivePlan.Items,
            item => Assert.Equal(PlanItemOutcome.Conflict, item.Outcome));
        Assert.All(
            sensitivePlan.Items,
            item => Assert.Equal(PlanItemOutcome.Ready, item.Outcome));
    }

    [Fact]
    public void UnicodeEquivalentTargetsConflictOnlyWhenNormalizationIsSelected()
    {
        const string composed = "caf\u00e9.txt";
        const string decomposed = "cafe\u0301.txt";
        PathPolicy normalized = Policy(caseSensitive: true, normalizeUnicode: true);
        PathPolicy ordinal = Policy(caseSensitive: true, normalizeUnicode: false);

        OrganizationPlan normalizedPlan = CompleteMissing(
            Prepare(
                normalized,
                [
                    Entry(normalized, $@"C:\Inbox\one\{composed}"),
                    Entry(normalized, $@"C:\Inbox\two\{decomposed}"),
                ],
                @"C:\Archive"));
        OrganizationPlan ordinalPlan = CompleteMissing(
            Prepare(
                ordinal,
                [
                    Entry(ordinal, $@"C:\Inbox\one\{composed}"),
                    Entry(ordinal, $@"C:\Inbox\two\{decomposed}"),
                ],
                @"C:\Archive"));

        Assert.All(
            normalizedPlan.Items,
            item => Assert.Equal(PlanItemOutcome.Conflict, item.Outcome));
        Assert.All(
            ordinalPlan.Items,
            item => Assert.Equal(PlanItemOutcome.Ready, item.Outcome));
    }

    [Fact]
    public void SnapshotPolicyMismatchBlocksEveryCandidate()
    {
        PathPolicy insensitive = Policy(caseSensitive: false, normalizeUnicode: true);
        PathPolicy sensitive = Policy(caseSensitive: true, normalizeUnicode: true);
        OrganizationPlanPreparation preparation = SinglePreparation(insensitive);

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            Snapshot(sensitive, []));

        Assert.Equal(PlanItemOutcome.Blocked, Assert.Single(plan.Items).Outcome);
        Assert.Equal(
            PlanReasonKind.DestinationSnapshotPolicyMismatch,
            Assert.Single(plan.Items[0].Reasons).Kind);
        Assert.Equal(
            OrganizationPlanDiagnosticKind.DestinationSnapshotPolicyMismatch,
            Assert.Single(plan.Diagnostics).Kind);
    }

    [Fact]
    public void DestinationSnapshotCopiesAndProtectsCallerOwnedFacts()
    {
        PathPolicy policy = Policy(caseSensitive: false, normalizeUnicode: true);
        CanonicalPath target = Path(policy, @"C:\Archive\a.txt");
        DestinationPathFact original = new(target, DestinationEntryKind.Missing);
        List<DestinationPathFact> facts = [original];

        DestinationStateSnapshot snapshot = Snapshot(policy, facts);
        facts.Clear();

        Assert.Same(original, Assert.Single(snapshot.Facts));
        Assert.Throws<NotSupportedException>(
            () => ((IList<DestinationPathFact>)snapshot.Facts).Clear());
    }

    [Fact]
    public void DestinationFactOrderDoesNotChangeThePlan()
    {
        PathPolicy policy = Policy(caseSensitive: true, normalizeUnicode: false);
        OrganizationPlanPreparation preparation = Prepare(
            policy,
            [
                Entry(policy, @"C:\Inbox\b.txt"),
                Entry(policy, @"C:\Inbox\a.txt"),
            ],
            @"C:\Archive");
        DestinationPathFact[] facts = SuccessfulFacts(preparation).ToArray();

        OrganizationPlan forward = OrganizationPlanner.Complete(
            preparation,
            Snapshot(policy, facts));
        OrganizationPlan reversed = OrganizationPlanner.Complete(
            preparation,
            Snapshot(policy, facts.Reverse()));

        Assert.Equal(Projection(forward), Projection(reversed));
        Assert.Equal(
            [@"C:\Inbox\a.txt", @"C:\Inbox\b.txt"],
            forward.Items.Select(item => item.Source.FullPath));
    }

    private static OrganizationPlanPreparation SinglePreparation(PathPolicy policy) =>
        Prepare(policy, [Entry(policy, @"C:\Inbox\a.txt")], @"C:\Archive");

    private static OrganizationPlanPreparation Prepare(
        PathPolicy policy,
        IEnumerable<FileEntry> files,
        string destination)
    {
        FileScanResult scan = new(Path(policy, @"C:\Inbox"), files, []);
        OrganizationRuleSet rules = new(
        [
            new OrganizationRule(
                new Rule("move", StubCondition.Matching()),
                new MoveToDirectoryAction(destination)),
        ]);

        return new OrganizationPlanner(new FixedTimeProvider(Now)).Prepare(scan, rules, policy);
    }

    private static OrganizationPlan CompleteMissing(
        OrganizationPlanPreparation preparation) =>
        OrganizationPlanner.Complete(
            preparation,
            new DestinationStateSnapshot(
                preparation.PathSemantics,
                SuccessfulFacts(preparation)));

    private static IEnumerable<DestinationPathFact> SuccessfulFacts(
        OrganizationPlanPreparation preparation)
    {
        PathPolicy policy = new(
            preparation.PathSemantics.Syntax,
            preparation.PathSemantics.CaseSensitivity,
            preparation.PathSemantics.UnicodeNormalization);
        HashSet<CanonicalPath> directories = new(
            preparation.DestinationDirectories,
            policy.LogicalComparer);

        return preparation.DestinationPaths.Select(
            path => new DestinationPathFact(
                path,
                directories.Contains(path)
                    ? DestinationEntryKind.Directory
                    : DestinationEntryKind.Missing));
    }

    private static IEnumerable<DestinationPathFact> DirectoryFacts(
        OrganizationPlanPreparation preparation) =>
        preparation.DestinationDirectories.Select(
            path => new DestinationPathFact(path, DestinationEntryKind.Directory));

    private static DestinationStateSnapshot Snapshot(
        PathPolicy policy,
        IEnumerable<DestinationPathFact> facts) =>
        new(policy.Semantics, facts);

    private static FileEntry Entry(PathPolicy policy, string path) =>
        new(Path(policy, path), 1, null, Now.AddDays(-1));

    private static CanonicalPath Path(PathPolicy policy, string value) =>
        policy.CanonicalizeAbsolute(value).Path
        ?? throw new InvalidOperationException($"Invalid test path: {value}");

    private static PathPolicy Policy(bool caseSensitive, bool normalizeUnicode) =>
        new(
            PathSyntax.Windows,
            caseSensitive ? PathCaseSensitivity.Sensitive : PathCaseSensitivity.Insensitive,
            normalizeUnicode
                ? PathUnicodeNormalization.FormC
                : PathUnicodeNormalization.None);

    private static string[] Projection(OrganizationPlan plan) =>
        plan.Items.Select(
                item => string.Join(
                    "|",
                    item.Source.FullPath,
                    item.ProposedTargetPath?.Value,
                    item.Outcome,
                    string.Join(",", item.Reasons.Select(reason => reason.Kind))))
            .ToArray();
}
