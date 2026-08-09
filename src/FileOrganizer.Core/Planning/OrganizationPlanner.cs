using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// Pure, deterministic rule evaluation and conflict analysis for dry-run plans.
/// </summary>
/// <remarks>
/// This type has no file-system port. Destination state must be captured at an
/// explicit Infrastructure boundary and supplied to <see cref="Complete"/>.
/// </remarks>
public sealed class OrganizationPlanner
{
    private readonly TimeProvider _timeProvider;

    public OrganizationPlanner(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Evaluates rules and resolves candidate targets without inspecting or
    /// modifying the file system.
    /// </summary>
    public OrganizationPlanPreparation Prepare(
        FileScanResult scan,
        OrganizationRuleSet rules,
        PathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(pathPolicy);

        EvaluationContext context = new(_timeProvider);
        Dictionary<OrganizationRule, IReadOnlyList<ActionValidationProblem>> actionProblems =
            BuildActionProblemMap(rules.ValidateActions(pathPolicy));
        FileEntry[] sources = [.. scan.Files];
        Array.Sort(sources, new SourceComparer(pathPolicy));

        PreparedPlanItem[] items = sources
            .Select(
                source => PrepareItem(
                    scan.CanonicalRoot,
                    source,
                    rules,
                    actionProblems,
                    pathPolicy,
                    context))
            .ToArray();

        CanonicalPath[] destinationDirectories = UniquePaths(
            items
                .Where(item => item.Outcome == PlanItemOutcome.Ready)
                .Select(item => item.DestinationDirectory!),
            pathPolicy);
        CanonicalPath[] proposedTargetPaths = UniquePaths(
            items
                .Where(item => item.Outcome == PlanItemOutcome.Ready)
                .Select(item => item.ProposedTargetPath!),
            pathPolicy);
        CanonicalPath[] destinations = UniquePaths(
            destinationDirectories.Concat(proposedTargetPaths),
            pathPolicy);

        return new OrganizationPlanPreparation(
            context.UtcNow,
            scan.CanonicalRoot,
            scan.IsComplete,
            scan.Diagnostics,
            pathPolicy,
            items,
            destinationDirectories,
            proposedTargetPaths,
            destinations);
    }

    /// <summary>
    /// Combines a preparation with explicit read-only destination facts. This
    /// method performs no hidden live inspection and never assumes a missing
    /// fact means that a path is absent.
    /// </summary>
    public static OrganizationPlan Complete(
        OrganizationPlanPreparation preparation,
        DestinationStateSnapshot destinationState)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(destinationState);

        PathPolicy policy = preparation.PathPolicy;
        bool policyMismatch = preparation.PathSemantics != destinationState.PathSemantics;

        List<OrganizationPlanDiagnostic> diagnostics = [];
        if (!preparation.IsScanComplete)
        {
            diagnostics.Add(
                new OrganizationPlanDiagnostic(
                    OrganizationPlanDiagnosticKind.IncompleteScan,
                    "The source scan was incomplete, so this preview is not eligible for execution."));
        }

        if (!preparation.IsSourceRootCompatible)
        {
            diagnostics.Add(
                new OrganizationPlanDiagnostic(
                    OrganizationPlanDiagnosticKind.SourcePathPolicyMismatch,
                    "The source root was canonicalized under different path semantics."));
        }

        if (policyMismatch && preparation.DestinationPaths.Count != 0)
        {
            diagnostics.Add(
                new OrganizationPlanDiagnostic(
                    OrganizationPlanDiagnosticKind.DestinationSnapshotPolicyMismatch,
                    "Destination facts were captured under different path semantics."));
        }

        Dictionary<CanonicalPath, List<PreparedPlanItem>> targetGroups =
            BuildTargetGroups(preparation.Items, policy);

        Dictionary<CanonicalPath, List<DestinationPathFact>> factGroups = policyMismatch
            ? new Dictionary<CanonicalPath, List<DestinationPathFact>>(policy.LogicalComparer)
            : BuildFactGroups(destinationState.Facts, policy);

        OrganizationPlanItem[] finalItems = preparation.Items
            .Select(
                item => CompleteItem(
                    item,
                    targetGroups,
                    factGroups,
                    policyMismatch))
            .ToArray();

        return new OrganizationPlan(
            preparation.GeneratedAtUtc,
            preparation.SourceRoot,
            preparation.IsScanComplete,
            preparation.ScanDiagnostics,
            diagnostics,
            finalItems);
    }

    private static PreparedPlanItem PrepareItem(
        CanonicalPath sourceRoot,
        FileEntry source,
        OrganizationRuleSet rules,
        Dictionary<OrganizationRule, IReadOnlyList<ActionValidationProblem>> actionProblems,
        PathPolicy policy,
        EvaluationContext context)
    {
        SourcePrecondition precondition = new(source.Path, source);

        if (sourceRoot.Semantics != policy.Semantics || source.Path.Semantics != policy.Semantics)
        {
            return Prepared(
                source,
                precondition,
                outcome: PlanItemOutcome.Blocked,
                reasons:
                [
                    Reason(
                        PlanReasonKind.InvalidSourcePath,
                        "The source path was canonicalized under different path semantics."),
                ]);
        }

        if (policy.Relate(sourceRoot, source.Path) != PathRelationship.Descendant)
        {
            return Prepared(
                source,
                precondition,
                outcome: PlanItemOutcome.Blocked,
                reasons:
                [
                    Reason(
                        PlanReasonKind.SourceOutsideRoot,
                        "The source file is not below the declared scan root."),
                ]);
        }

        RuleSetEvaluation evaluation = rules.Evaluate(source, context);
        if (evaluation.Outcome == RuleSetOutcome.NoRuleApplies)
        {
            return Prepared(
                source,
                precondition,
                ruleEvaluation: evaluation,
                outcome: PlanItemOutcome.Skipped,
                reasons:
                [
                    Reason(
                        PlanReasonKind.NoApplicableRule,
                        "No enabled, non-excluded rule matched this file."),
                ]);
        }

        if (evaluation.Outcome == RuleSetOutcome.Blocked)
        {
            return Prepared(
                source,
                precondition,
                ruleEvaluation: evaluation,
                outcome: PlanItemOutcome.Blocked,
                reasons:
                [
                    Reason(
                        PlanReasonKind.RuleIndeterminate,
                        "A higher-priority rule could not reach a definite answer."),
                ]);
        }

        OrganizationRule selectedRule = rules.GetAssociation(evaluation.DecidingRule!);
        IReadOnlyList<ActionValidationProblem> selectedActionProblems =
            actionProblems.TryGetValue(selectedRule, out IReadOnlyList<ActionValidationProblem>? found)
                ? found
                : [];

        if (selectedActionProblems.Count != 0)
        {
            PlanReason[] reasons = selectedActionProblems
                .Select(
                    problem => Reason(
                        PlanReasonKind.InvalidAction,
                        problem.Description,
                        actionProblem: problem))
                .ToArray();

            return Prepared(
                source,
                precondition,
                evaluation,
                selectedRule,
                selectedRule.Action,
                outcome: PlanItemOutcome.Blocked,
                reasons: reasons);
        }

        TargetResolution target = ResolveTarget(
            sourceRoot,
            source,
            selectedRule.Action,
            policy);

        if (target.Path is null)
        {
            return Prepared(
                source,
                precondition,
                evaluation,
                selectedRule,
                selectedRule.Action,
                outcome: PlanItemOutcome.Blocked,
                reasons: [target.Failure!]);
        }

        if (policy.AreEquivalent(source.Path, target.Path))
        {
            return Prepared(
                source,
                precondition,
                evaluation,
                selectedRule,
                selectedRule.Action,
                target.Path,
                target.DestinationDirectory,
                PlanItemOutcome.Skipped,
                [
                    Reason(
                        PlanReasonKind.SourceEqualsTarget,
                        "The proposed target is the same logical path as the source."),
                ]);
        }

        return Prepared(
            source,
            precondition,
            evaluation,
            selectedRule,
            selectedRule.Action,
            target.Path,
            target.DestinationDirectory,
            PlanItemOutcome.Ready,
            []);
    }

    private static TargetResolution ResolveTarget(
        CanonicalPath sourceRoot,
        FileEntry source,
        OrganizationAction action,
        PathPolicy policy)
    {
        CanonicalPath? containmentRoot;
        CanonicalPath? destinationDirectory;
        PathResolution target;

        switch (action)
        {
            case MoveToDirectoryAction move:
                destinationDirectory = policy.CanonicalizeAbsolute(move.DestinationDirectory).Path;
                containmentRoot = destinationDirectory;
                target = policy.AppendFileName(destinationDirectory!, source.Name);
                break;

            case CopyToDirectoryAction copy:
                destinationDirectory = policy.CanonicalizeAbsolute(copy.DestinationDirectory).Path;
                containmentRoot = destinationDirectory;
                target = policy.AppendFileName(destinationDirectory!, source.Name);
                break;

            case RenameToLiteralFileNameAction rename:
                containmentRoot = sourceRoot;
                destinationDirectory = policy.GetParent(source.Path);
                if (destinationDirectory is null)
                {
                    return TargetResolution.Failed(
                        Reason(
                            PlanReasonKind.InvalidTargetPath,
                            "A target could not be built for a source at the path root."));
                }

                target = policy.AppendFileName(destinationDirectory, rename.FileName);
                break;

            default:
                return TargetResolution.Failed(
                    Reason(
                        PlanReasonKind.InvalidAction,
                        "The selected action kind is not supported by this planner."));
        }

        if (!target.Succeeded)
        {
            return TargetResolution.Failed(
                Reason(
                    PlanReasonKind.InvalidTargetPath,
                    target.Problem!.Description,
                    pathProblem: target.Problem));
        }

        if (policy.Relate(containmentRoot!, target.Path!) != PathRelationship.Descendant)
        {
            return TargetResolution.Failed(
                Reason(
                    PlanReasonKind.TargetOutsideRoot,
                    "The proposed target is not below the root that bounds this action."));
        }

        return TargetResolution.Succeeded(target.Path!, destinationDirectory!);
    }

    private static OrganizationPlanItem CompleteItem(
        PreparedPlanItem prepared,
        Dictionary<CanonicalPath, List<PreparedPlanItem>> targetGroups,
        Dictionary<CanonicalPath, List<DestinationPathFact>> factGroups,
        bool policyMismatch)
    {
        if (prepared.Outcome != PlanItemOutcome.Ready)
        {
            return Final(prepared, prepared.Outcome, prepared.Reasons);
        }

        CanonicalPath target = prepared.ProposedTargetPath!;
        List<PlanReason> reasons = [.. prepared.Reasons];
        bool blocked = false;
        bool conflict = false;

        if (targetGroups[target].Count > 1)
        {
            conflict = true;
            reasons.Add(
                Reason(
                    PlanReasonKind.DuplicateDestination,
                    "Multiple plan items propose the same logical destination.",
                    affectedPath: target));
        }

        if (policyMismatch)
        {
            blocked = true;
            reasons.Add(
                Reason(
                    PlanReasonKind.DestinationSnapshotPolicyMismatch,
                    "The target cannot be compared with facts from a different path policy.",
                    affectedPath: target));
        }
        else
        {
            EvaluateDestinationDirectory(
                prepared.DestinationDirectory!,
                factGroups,
                reasons,
                ref blocked);
            EvaluateTarget(
                target,
                factGroups,
                reasons,
                ref blocked,
                ref conflict);
        }

        PlanItemOutcome outcome = blocked
            ? PlanItemOutcome.Blocked
            : conflict
                ? PlanItemOutcome.Conflict
                : PlanItemOutcome.Ready;

        PlanReason[] orderedReasons = reasons
            .OrderBy(reason => reason.Kind)
            .ThenBy(reason => reason.Description, StringComparer.Ordinal)
            .ThenBy(reason => reason.AffectedPath?.Value, StringComparer.Ordinal)
            .ToArray();

        return Final(prepared, outcome, orderedReasons);
    }

    private static void EvaluateDestinationDirectory(
        CanonicalPath directory,
        Dictionary<CanonicalPath, List<DestinationPathFact>> factGroups,
        List<PlanReason> reasons,
        ref bool blocked)
    {
        DestinationPathFact? fact = FindUnambiguousFact(
            directory,
            factGroups,
            reasons,
            ref blocked);
        if (fact is null)
        {
            return;
        }

        switch (fact.EntryKind)
        {
            case DestinationEntryKind.Directory:
                return;

            case DestinationEntryKind.Missing:
                blocked = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationDirectoryMissing,
                        "The directory required to contain the target does not exist.",
                        affectedPath: directory));
                return;

            case DestinationEntryKind.File:
                blocked = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationPathIsNotDirectory,
                        "A file exists where this action requires a destination directory.",
                        affectedPath: directory));
                return;

            case DestinationEntryKind.InspectionFailed:
                blocked = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationInspectionFailed,
                        fact.Failure!.Description,
                        destinationInspectionFailure: fact.Failure,
                        affectedPath: directory));
                return;
        }
    }

    private static void EvaluateTarget(
        CanonicalPath target,
        Dictionary<CanonicalPath, List<DestinationPathFact>> factGroups,
        List<PlanReason> reasons,
        ref bool blocked,
        ref bool conflict)
    {
        DestinationPathFact? fact = FindUnambiguousFact(
            target,
            factGroups,
            reasons,
            ref blocked);
        if (fact is null)
        {
            return;
        }

        switch (fact.EntryKind)
        {
            case DestinationEntryKind.Missing:
                return;

            case DestinationEntryKind.File:
                conflict = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationFileExists,
                        "A file already exists at the proposed destination.",
                        affectedPath: target));
                return;

            case DestinationEntryKind.Directory:
                conflict = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationDirectoryExists,
                        "A directory already exists at the proposed file destination.",
                        affectedPath: target));
                return;

            case DestinationEntryKind.InspectionFailed:
                blocked = true;
                reasons.Add(
                    Reason(
                        PlanReasonKind.DestinationInspectionFailed,
                        fact.Failure!.Description,
                        destinationInspectionFailure: fact.Failure,
                        affectedPath: target));
                return;
        }
    }

    private static DestinationPathFact? FindUnambiguousFact(
        CanonicalPath path,
        Dictionary<CanonicalPath, List<DestinationPathFact>> factGroups,
        List<PlanReason> reasons,
        ref bool blocked)
    {
        if (!factGroups.TryGetValue(path, out List<DestinationPathFact>? facts))
        {
            blocked = true;
            reasons.Add(
                Reason(
                    PlanReasonKind.DestinationStateMissing,
                    "No destination fact was supplied for a required path.",
                    affectedPath: path));
            return null;
        }

        if (!HaveEquivalentState(facts))
        {
            blocked = true;
            reasons.Add(
                Reason(
                    PlanReasonKind.AmbiguousDestinationState,
                    "Equivalent destination facts disagree about what exists.",
                    affectedPath: path));
            return null;
        }

        return facts[0];
    }

    private static bool HaveEquivalentState(List<DestinationPathFact> facts)
    {
        DestinationPathFact first = facts[0];
        return facts.All(
            fact => fact.EntryKind == first.EntryKind
                && fact.Failure == first.Failure);
    }

    private static Dictionary<CanonicalPath, List<PreparedPlanItem>> BuildTargetGroups(
        IEnumerable<PreparedPlanItem> items,
        PathPolicy policy)
    {
        Dictionary<CanonicalPath, List<PreparedPlanItem>> groups =
            new(policy.LogicalComparer);

        foreach (PreparedPlanItem item in items.Where(
                     item => item.Outcome == PlanItemOutcome.Ready))
        {
            AddToGroup(groups, item.ProposedTargetPath!, item);
        }

        return groups;
    }

    private static Dictionary<CanonicalPath, List<DestinationPathFact>> BuildFactGroups(
        IEnumerable<DestinationPathFact> facts,
        PathPolicy policy)
    {
        Dictionary<CanonicalPath, List<DestinationPathFact>> groups =
            new(policy.LogicalComparer);

        foreach (DestinationPathFact fact in facts)
        {
            AddToGroup(groups, fact.Path, fact);
        }

        return groups;
    }

    private static Dictionary<OrganizationRule, IReadOnlyList<ActionValidationProblem>>
        BuildActionProblemMap(IEnumerable<OrganizationRuleActionProblem> problems)
    {
        Dictionary<OrganizationRule, List<ActionValidationProblem>> mutable = [];
        foreach (OrganizationRuleActionProblem problem in problems)
        {
            if (!mutable.TryGetValue(
                    problem.OrganizationRule,
                    out List<ActionValidationProblem>? ruleProblems))
            {
                ruleProblems = [];
                mutable.Add(problem.OrganizationRule, ruleProblems);
            }

            ruleProblems.Add(problem.Problem);
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ActionValidationProblem>)Array.AsReadOnly(pair.Value.ToArray()));
    }

    private static CanonicalPath[] UniquePaths(
        IEnumerable<CanonicalPath> paths,
        PathPolicy policy) =>
        paths
            .OrderBy(path => path, policy.DeterministicComparer)
            .Distinct(policy.LogicalComparer)
            .ToArray();

    private static void AddToGroup<T>(
        Dictionary<CanonicalPath, List<T>> groups,
        CanonicalPath key,
        T item)
    {
        if (!groups.TryGetValue(key, out List<T>? group))
        {
            group = [];
            groups.Add(key, group);
        }

        group.Add(item);
    }

    private static PreparedPlanItem Prepared(
        FileEntry source,
        SourcePrecondition sourcePrecondition,
        RuleSetEvaluation? ruleEvaluation = null,
        OrganizationRule? selectedRule = null,
        OrganizationAction? proposedAction = null,
        CanonicalPath? proposedTargetPath = null,
        CanonicalPath? destinationDirectory = null,
        PlanItemOutcome outcome = PlanItemOutcome.Blocked,
        IEnumerable<PlanReason>? reasons = null) =>
        new(
            source,
            sourcePrecondition,
            ruleEvaluation,
            selectedRule,
            proposedAction,
            proposedTargetPath,
            destinationDirectory,
            outcome,
            reasons ?? []);

    private static OrganizationPlanItem Final(
        PreparedPlanItem prepared,
        PlanItemOutcome outcome,
        IEnumerable<PlanReason> reasons) =>
        new(
            prepared.Source,
            prepared.SourcePrecondition,
            prepared.RuleEvaluation,
            prepared.SelectedRule,
            prepared.ProposedAction,
            prepared.ProposedTargetPath,
            prepared.DestinationDirectory,
            outcome,
            reasons);

    private static PlanReason Reason(
        PlanReasonKind kind,
        string description,
        ActionValidationProblem? actionProblem = null,
        PathProblem? pathProblem = null,
        DestinationInspectionFailure? destinationInspectionFailure = null,
        CanonicalPath? affectedPath = null) =>
        new(
            kind,
            description,
            actionProblem,
            pathProblem,
            destinationInspectionFailure,
            affectedPath);

    private sealed class SourceComparer(PathPolicy policy) : IComparer<FileEntry>
    {
        public int Compare(FileEntry? left, FileEntry? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int path = policy.DeterministicComparer.Compare(left.Path, right.Path);
            if (path != 0)
            {
                return path;
            }

            int length = left.SizeInBytes.CompareTo(right.SizeInBytes);
            if (length != 0)
            {
                return length;
            }

            int created = Nullable.Compare(left.CreatedUtc, right.CreatedUtc);
            return created != 0
                ? created
                : left.ModifiedUtc.CompareTo(right.ModifiedUtc);
        }
    }

    private sealed record TargetResolution(
        CanonicalPath? Path,
        CanonicalPath? DestinationDirectory,
        PlanReason? Failure)
    {
        public static TargetResolution Succeeded(
            CanonicalPath path,
            CanonicalPath destinationDirectory) =>
            new(path, destinationDirectory, null);

        public static TargetResolution Failed(PlanReason failure) => new(null, null, failure);
    }
}
