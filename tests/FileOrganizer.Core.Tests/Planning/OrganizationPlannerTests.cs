using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;
using FileOrganizer.Core.Tests.Matching;

namespace FileOrganizer.Core.Tests.Planning;

public sealed class OrganizationPlannerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 10, 11, 12, TimeSpan.Zero);

    private static readonly PathPolicy Windows = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public void ProducesAReadyMoveWithItsAuditAndSourcePreconditions()
    {
        DateTimeOffset created = Now.AddDays(-20);
        DateTimeOffset modified = Now.AddDays(-2);
        FileEntry source = Entry(
            Windows,
            @"C:\Inbox\report.final.pdf",
            length: 42,
            created: created,
            modified: modified);
        OrganizationRule selected = Matching(new MoveToDirectoryAction(@"C:\Archive"));

        OrganizationPlanPreparation preparation = Prepare(
            Windows,
            @"C:\Inbox",
            [source],
            [selected]);
        Assert.Equal(@"C:\Archive", Assert.Single(preparation.DestinationDirectories).Value);
        Assert.Equal(
            @"C:\Archive\report.final.pdf",
            Assert.Single(preparation.ProposedTargetPaths).Value);
        Assert.Equal(2, preparation.DestinationPaths.Count);
        OrganizationPlan plan = CompleteAsMissing(preparation);

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Ready, item.Outcome);
        Assert.Same(selected, item.SelectedRule);
        Assert.Same(selected.Action, item.ProposedAction);
        Assert.Equal(@"C:\Archive\report.final.pdf", item.ProposedTargetPath?.Value);
        Assert.Equal(@"C:\Archive", item.DestinationDirectory?.Value);
        Assert.Equal(RuleSetOutcome.RuleSelected, item.RuleEvaluation?.Outcome);
        Assert.Equal(source.Path.Value, item.SourcePrecondition.CanonicalPath.Value);
        Assert.Equal(42, item.SourcePrecondition.Length);
        Assert.Equal(created, item.SourcePrecondition.CreatedUtc);
        Assert.Equal(modified, item.SourcePrecondition.ModifiedUtc);
        Assert.Empty(item.Reasons);
        Assert.Equal(Now, plan.GeneratedAtUtc);
        Assert.True(plan.IsEligibleForExecution);
    }

    [Fact]
    public void PreservesDisabledExcludedAndNoMatchOutcomesWhenNothingApplies()
    {
        OrganizationRuleSet rules = new(
        [
            new OrganizationRule(
                new Rule("disabled", StubCondition.Matching(), isEnabled: false, order: 0),
                new MoveToDirectoryAction(@"C:\One")),
            new OrganizationRule(
                new Rule(
                    "excluded",
                    StubCondition.Matching(),
                    StubCondition.Matching(),
                    order: 1),
                new MoveToDirectoryAction(@"C:\Two")),
            new OrganizationRule(
                new Rule("not matched", StubCondition.NotMatching(), order: 2),
                new MoveToDirectoryAction(@"C:\Three")),
        ]);

        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(Now)).Prepare(
                Scan(Windows, @"C:\Inbox", [Entry(Windows, @"C:\Inbox\a.txt")]),
                rules,
                Windows);
        OrganizationPlan plan = CompleteAsMissing(preparation);

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Skipped, item.Outcome);
        Assert.Null(item.SelectedRule);
        Assert.Equal(
            [RuleOutcome.Disabled, RuleOutcome.Excluded, RuleOutcome.NotMatched],
            item.RuleEvaluation!.Evaluations.Select(evaluation => evaluation.Outcome));
        Assert.Equal(PlanReasonKind.NoApplicableRule, Assert.Single(item.Reasons).Kind);
        Assert.True(plan.IsEligibleForExecution);
    }

    [Fact]
    public void BlocksAtAHigherPriorityIndeterminateRule()
    {
        OrganizationRule blocked = new(
            new Rule("needs creation time", StubCondition.Undetermined("creation unavailable")),
            new MoveToDirectoryAction(@"C:\Archive"));
        OrganizationRule later = Matching(new MoveToDirectoryAction(@"C:\Later"), order: 1);

        OrganizationPlan plan = CompleteAsMissing(
            Prepare(
                Windows,
                @"C:\Inbox",
                [Entry(Windows, @"C:\Inbox\a.txt")],
                [blocked, later]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        Assert.Null(item.SelectedRule);
        Assert.Equal(RuleSetOutcome.Blocked, item.RuleEvaluation?.Outcome);
        Assert.Equal(
            EvaluationProblemKind.MetadataUnavailable,
            Assert.Single(item.RuleEvaluation!.Problems).Kind);
        Assert.Equal(PlanReasonKind.RuleIndeterminate, Assert.Single(item.Reasons).Kind);
        Assert.False(plan.IsEligibleForExecution);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:\..\escaped")]
    public void BlocksASelectedActionWithAnInvalidDestination(string destination)
    {
        OrganizationPlan plan = CompleteAsMissing(
            Prepare(
                Windows,
                @"C:\Inbox",
                [Entry(Windows, @"C:\Inbox\a.txt")],
                [Matching(new CopyToDirectoryAction(destination))]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        PlanReason reason = Assert.Single(item.Reasons);
        Assert.Equal(PlanReasonKind.InvalidAction, reason.Kind);
        Assert.Equal(
            ActionValidationProblemKind.InvalidDestinationDirectory,
            reason.ActionProblem?.Kind);
        Assert.NotNull(reason.ActionProblem?.PathProblem);
        Assert.NotNull(item.SelectedRule);
        Assert.Null(item.ProposedTargetPath);
    }

    [Fact]
    public void BlocksASiblingPrefixSourceOutsideTheDeclaredRoot()
    {
        OrganizationPlan plan = CompleteAsMissing(
            Prepare(
                Windows,
                @"C:\Data",
                [Entry(Windows, @"C:\Database\a.txt")],
                [Matching(new MoveToDirectoryAction(@"C:\Archive"))]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Blocked, item.Outcome);
        Assert.Equal(PlanReasonKind.SourceOutsideRoot, Assert.Single(item.Reasons).Kind);
        Assert.Null(item.RuleEvaluation);
    }

    [Fact]
    public void AllowsADestinationDirectoryInsideTheSourceRoot()
    {
        OrganizationPlan plan = CompleteAsMissing(
            Prepare(
                Windows,
                @"C:\Downloads",
                [Entry(Windows, @"C:\Downloads\photo.jpg")],
                [Matching(new MoveToDirectoryAction(@"C:\Downloads\Images"))]));

        OrganizationPlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanItemOutcome.Ready, item.Outcome);
        Assert.Equal(@"C:\Downloads\Images\photo.jpg", item.ProposedTargetPath?.Value);
    }

    [Fact]
    public void TreatsASourceEqualTargetAsAnExplicitSkippedNoOp()
    {
        OrganizationPlanPreparation preparation = Prepare(
            Windows,
            @"C:\Inbox",
            [Entry(Windows, @"C:\Inbox\Report.txt")],
            [Matching(new RenameToLiteralFileNameAction("report.txt"))]);

        Assert.Empty(preparation.DestinationPaths);

        OrganizationPlanItem item = Assert.Single(CompleteAsMissing(preparation).Items);
        Assert.Equal(PlanItemOutcome.Skipped, item.Outcome);
        Assert.Equal(PlanReasonKind.SourceEqualsTarget, Assert.Single(item.Reasons).Kind);
        Assert.Equal(@"C:\Inbox\report.txt", item.ProposedTargetPath?.Value);
    }

    [Fact]
    public void KeepsAMultipleDotRenameLiteralUninterpreted()
    {
        OrganizationPlan plan = CompleteAsMissing(
            Prepare(
                Windows,
                @"C:\Inbox",
                [Entry(Windows, @"C:\Inbox\archive.tar.gz")],
                [Matching(new RenameToLiteralFileNameAction("archive.final.tar.gz"))]));

        Assert.Equal(
            @"C:\Inbox\archive.final.tar.gz",
            Assert.Single(plan.Items).ProposedTargetPath?.Value);
    }

    [Fact]
    public void IncompleteScanKeepsPreviewButHardDisablesExecutionEligibility()
    {
        FileScanResult scan = Scan(
            Windows,
            @"C:\Inbox",
            [Entry(Windows, @"C:\Inbox\a.txt")],
            [new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, @"C:\Inbox\private")]);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(Now)).Prepare(
                scan,
                new OrganizationRuleSet(
                    [Matching(new MoveToDirectoryAction(@"C:\Archive"))]),
                Windows);

        OrganizationPlan plan = CompleteAsMissing(preparation);

        Assert.False(plan.IsScanComplete);
        Assert.Equal(PlanItemOutcome.Ready, Assert.Single(plan.Items).Outcome);
        Assert.Equal(
            OrganizationPlanDiagnosticKind.IncompleteScan,
            Assert.Single(plan.Diagnostics).Kind);
        Assert.Equal(ScanDiagnosticKind.AccessDenied, Assert.Single(plan.ScanDiagnostics).Kind);
        Assert.False(plan.IsEligibleForExecution);
        Assert.Throws<NotSupportedException>(
            () => ((IList<OrganizationPlanDiagnostic>)plan.Diagnostics).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<ScanDiagnostic>)plan.ScanDiagnostics).Clear());
    }

    [Fact]
    public void EmptyCompleteScanWithDifferentRootSemanticsIsNotEligible()
    {
        PathPolicy posix = new(
            PathSyntax.Posix,
            PathCaseSensitivity.Sensitive,
            PathUnicodeNormalization.None);
        FileScanResult scan = new(Path(posix, "/inbox"), [], []);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(Now)).Prepare(
                scan,
                new OrganizationRuleSet([]),
                Windows);

        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            new DestinationStateSnapshot(Windows.Semantics, []));

        Assert.False(preparation.IsSourceRootCompatible);
        Assert.Empty(plan.Items);
        Assert.Equal(
            OrganizationPlanDiagnosticKind.SourcePathPolicyMismatch,
            Assert.Single(plan.Diagnostics).Kind);
        Assert.False(plan.IsEligibleForExecution);
    }

    [Fact]
    public void ReadsThePlanningClockOnlyOnceForEveryFile()
    {
        CountingTimeProvider time = new(Now);
        OrganizationPlanner planner = new(time);
        OrganizationRule ageRule = new(
            new Rule(
                "old",
                new AgeCondition(
                    TimestampField.Modified,
                    ComparisonOperator.GreaterThan,
                    TimeSpan.FromDays(1))),
            new MoveToDirectoryAction(@"C:\Archive"));

        OrganizationPlanPreparation preparation = planner.Prepare(
            Scan(
                Windows,
                @"C:\Inbox",
                [
                    Entry(Windows, @"C:\Inbox\a.txt", modified: Now.AddDays(-2)),
                    Entry(Windows, @"C:\Inbox\b.txt", modified: Now.AddDays(-3)),
                ]),
            new OrganizationRuleSet([ageRule]),
            Windows);

        OrganizationPlan plan = CompleteAsMissing(preparation);

        Assert.Equal(1, time.ReadCount);
        Assert.Equal(Now, plan.GeneratedAtUtc);
        Assert.All(plan.Items, item => Assert.Equal(PlanItemOutcome.Ready, item.Outcome));
    }

    [Fact]
    public void SnapshotsRuleAndPlanCollectionsAndExposesReadOnlyViews()
    {
        OrganizationRule original = Matching(new MoveToDirectoryAction(@"C:\Archive"));
        OrganizationRule[] input = [original];
        OrganizationRuleSet ruleSet = new(input);
        input[0] = Matching(new MoveToDirectoryAction(@"C:\Elsewhere"));

        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(Now)).Prepare(
                Scan(Windows, @"C:\Inbox", [Entry(Windows, @"C:\Inbox\a.txt")]),
                ruleSet,
                Windows);
        OrganizationPlan plan = CompleteAsMissing(preparation);

        Assert.Same(original, Assert.Single(ruleSet.Rules));
        Assert.Throws<NotSupportedException>(() => ((IList<OrganizationRule>)ruleSet.Rules).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<CanonicalPath>)preparation.DestinationPaths).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<CanonicalPath>)preparation.DestinationDirectories).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<CanonicalPath>)preparation.ProposedTargetPaths).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<OrganizationPlanItem>)plan.Items).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<PlanReason>)Assert.Single(plan.Items).Reasons).Clear());
    }

    [Fact]
    public void ReportsInvalidActionsAcrossTheWholeRuleSetBeforeAnyFileMatches()
    {
        OrganizationRule invalid = new(
            new Rule("disabled", StubCondition.Matching(), isEnabled: false),
            new MoveToDirectoryAction("relative"));
        OrganizationRuleSet ruleSet = new([invalid]);

        IReadOnlyList<OrganizationRuleActionProblem> problems =
            ruleSet.ValidateActions(Windows);

        OrganizationRuleActionProblem problem = Assert.Single(problems);
        Assert.Same(invalid, problem.OrganizationRule);
        Assert.Equal(
            ActionValidationProblemKind.InvalidDestinationDirectory,
            problem.Problem.Kind);
        Assert.Equal(PathProblemKind.NotFullyQualified, problem.Problem.PathProblem.Kind);
        Assert.Throws<NotSupportedException>(
            () => ((IList<OrganizationRuleActionProblem>)problems).Clear());
    }

    [Fact]
    public void ReversingDistinctlyOrderedRuleInputDoesNotChangeSelection()
    {
        OrganizationRule winner = Matching(
            new MoveToDirectoryAction(@"C:\First"),
            order: -10);
        OrganizationRule later = Matching(
            new MoveToDirectoryAction(@"C:\Later"),
            order: 10);
        FileEntry source = Entry(Windows, @"C:\Inbox\a.txt");

        OrganizationPlan forward = CompleteAsMissing(
            Prepare(Windows, @"C:\Inbox", [source], [winner, later]));
        OrganizationPlan reversed = CompleteAsMissing(
            Prepare(Windows, @"C:\Inbox", [source], [later, winner]));

        Assert.Same(winner, Assert.Single(forward.Items).SelectedRule);
        Assert.Same(winner, Assert.Single(reversed.Items).SelectedRule);
        Assert.Equal(
            forward.Items[0].ProposedTargetPath?.Value,
            reversed.Items[0].ProposedTargetPath?.Value);
    }

    private static OrganizationPlanPreparation Prepare(
        PathPolicy policy,
        string root,
        IEnumerable<FileEntry> files,
        IEnumerable<OrganizationRule> rules) =>
        new OrganizationPlanner(new FixedTimeProvider(Now)).Prepare(
            Scan(policy, root, files),
            new OrganizationRuleSet(rules),
            policy);

    private static OrganizationPlan CompleteAsMissing(
        OrganizationPlanPreparation preparation) =>
        OrganizationPlanner.Complete(
            preparation,
            new DestinationStateSnapshot(
                preparation.PathSemantics,
                SuccessfulDestinationFacts(preparation)));

    private static IEnumerable<DestinationPathFact> SuccessfulDestinationFacts(
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

    private static FileScanResult Scan(
        PathPolicy policy,
        string root,
        IEnumerable<FileEntry> files,
        IEnumerable<ScanDiagnostic>? diagnostics = null) =>
        new(Path(policy, root), files, diagnostics ?? []);

    private static FileEntry Entry(
        PathPolicy policy,
        string path,
        long length = 1,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null) =>
        new(Path(policy, path), length, created, modified ?? Now.AddDays(-1));

    private static CanonicalPath Path(PathPolicy policy, string value) =>
        policy.CanonicalizeAbsolute(value).Path
        ?? throw new InvalidOperationException($"Invalid test path: {value}");

    private static OrganizationRule Matching(OrganizationAction action, int order = 0) =>
        new(new Rule("matching", StubCondition.Matching(), order: order), action);

    private sealed class CountingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return now;
        }
    }
}
