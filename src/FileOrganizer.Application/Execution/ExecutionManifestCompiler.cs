using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Execution;

/// <summary>
/// Compiles an eligible dry-run plan into the deterministic move-only execution surface.
/// </summary>
public static class ExecutionManifestCompiler
{
    /// <summary>
    /// Compiles only <see cref="PlanItemOutcome.Ready"/> items. The whole plan is
    /// rejected before a manifest exists when its safety gate is closed or any
    /// ready item requests an unsupported action.
    /// </summary>
    public static ExecutionManifestCompilation Compile(OrganizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<ExecutionManifestProblem> problems = [];
        OrganizationPlanItem[] readyItems = plan.Items
            .Where(item => item.Outcome == PlanItemOutcome.Ready)
            .ToArray();

        if (!plan.IsEligibleForExecution && HasNonIdentityEligibilityFailure(plan))
        {
            problems.Add(
                new ExecutionManifestProblem(
                    ExecutionManifestProblemKind.PlanNotEligible,
                    "The dry-run plan is not eligible for execution."));
        }

        PathSemantics semantics = plan.SourceRoot.Semantics;
        PathPolicy policy = new(
            semantics.Syntax,
            semantics.CaseSensitivity,
            semantics.UnicodeNormalization);
        Array.Sort(readyItems, new ReadyItemComparer(policy));

        ValidateRequiredPhysicalIdentities(plan, readyItems, problems);

        foreach (OrganizationPlanItem item in readyItems)
        {
            if (item.ProposedAction is MoveToDirectoryAction)
            {
                continue;
            }

            problems.Add(
                new ExecutionManifestProblem(
                    ExecutionManifestProblemKind.UnsupportedReadyAction,
                    "M3B-1 execution manifests support only move-to-directory actions.",
                    item.SourcePrecondition.CanonicalPath,
                    item.ProposedAction?.Kind));
        }

        ValidateReadyItems(plan.SourceRoot, readyItems, policy, problems);

        if (problems.Count != 0)
        {
            return ExecutionManifestCompilation.Rejected(problems);
        }

        ExecutionManifestItem[] manifestItems = new ExecutionManifestItem[readyItems.Length];
        for (int ordinal = 0; ordinal < readyItems.Length; ordinal++)
        {
            OrganizationPlanItem item = readyItems[ordinal];
            manifestItems[ordinal] = new ExecutionManifestItem(
                ordinal,
                item.SourcePrecondition,
                item.DestinationDirectory!,
                item.DestinationDirectoryPhysicalIdentity!,
                item.ProposedTargetPath!,
                item.SelectedRule!.Rule.Name,
                item.SelectedRule.Rule.Order);
        }

        PlanFingerprint fingerprint = ExecutionManifestFingerprint.Compute(
            plan.SourceRoot,
            plan.SourceRootPhysicalIdentity!,
            manifestItems,
            policy);

        return ExecutionManifestCompilation.Success(
            new ExecutionManifest(
                fingerprint,
                plan.GeneratedAtUtc,
                plan.SourceRoot,
                plan.SourceRootPhysicalIdentity!,
                manifestItems));
    }

    private static void ValidateRequiredPhysicalIdentities(
        OrganizationPlan plan,
        IEnumerable<OrganizationPlanItem> readyItems,
        List<ExecutionManifestProblem> problems)
    {
        if (plan.SourceRootPhysicalIdentity is null)
        {
            problems.Add(
                new ExecutionManifestProblem(
                    ExecutionManifestProblemKind.SourceRootPhysicalIdentityMissing,
                    "The scan root has no strong physical directory identity."));
        }

        foreach (OrganizationPlanItem item in readyItems)
        {
            if (item.SourcePrecondition.PhysicalIdentity is null)
            {
                problems.Add(
                    new ExecutionManifestProblem(
                        ExecutionManifestProblemKind.SourcePhysicalIdentityMissing,
                        "The ready source has no strong physical file identity.",
                        item.SourcePrecondition.CanonicalPath,
                        item.ProposedAction?.Kind));
            }

            if (item.DestinationDirectoryPhysicalIdentity is null)
            {
                problems.Add(
                    new ExecutionManifestProblem(
                        ExecutionManifestProblemKind.DestinationDirectoryPhysicalIdentityMissing,
                        "The ready operation has no strong destination-directory identity.",
                        item.SourcePrecondition.CanonicalPath,
                        item.ProposedAction?.Kind));
            }
        }
    }

    private static bool HasNonIdentityEligibilityFailure(OrganizationPlan plan) =>
        !plan.IsScanComplete
        || plan.Items.Any(
            item => item.Outcome is PlanItemOutcome.Blocked or PlanItemOutcome.Conflict)
        || plan.Diagnostics.Any(
            diagnostic => diagnostic.Kind
                != OrganizationPlanDiagnosticKind.StrongPhysicalIdentityUnavailable);

    private static void ValidateReadyItems(
        CanonicalPath sourceRoot,
        OrganizationPlanItem[] readyItems,
        PathPolicy policy,
        List<ExecutionManifestProblem> problems)
    {
        foreach (OrganizationPlanItem item in readyItems)
        {
            CanonicalPath source = item.SourcePrecondition.CanonicalPath;
            CanonicalPath destination = item.DestinationDirectory!;
            CanonicalPath target = item.ProposedTargetPath!;

            if (source.Semantics != policy.Semantics
                || destination.Semantics != policy.Semantics
                || target.Semantics != policy.Semantics)
            {
                AddInvalid(problems, item, "A ready item's paths use inconsistent path semantics.");
                continue;
            }

            if (policy.Relate(sourceRoot, source) != PathRelationship.Descendant)
            {
                AddInvalid(problems, item, "A ready source is not strictly contained by the source root.");
            }

            if (policy.Relate(destination, target) != PathRelationship.Descendant)
            {
                AddInvalid(
                    problems,
                    item,
                    "A ready target is not strictly contained by its destination directory.");
            }

            if (policy.AreEquivalent(source, target))
            {
                AddInvalid(problems, item, "A ready source and target identify the same logical path.");
            }

            PathResolution expectedTarget = policy.AppendFileName(destination, source.FileName);
            if (!expectedTarget.Succeeded
                || !policy.AreEquivalent(expectedTarget.Path!, target))
            {
                AddInvalid(
                    problems,
                    item,
                    "A ready target does not preserve the source file name under its destination directory.");
            }

            if (item.ProposedAction is MoveToDirectoryAction move)
            {
                PathResolution configuredDestination =
                    policy.CanonicalizeAbsolute(move.DestinationDirectory);
                if (!configuredDestination.Succeeded
                    || !policy.AreEquivalent(configuredDestination.Path!, destination))
                {
                    AddInvalid(
                        problems,
                        item,
                        "A ready destination does not match its move action configuration.");
                }
            }
        }

        AddDuplicateProblems(
            readyItems,
            item => item.SourcePrecondition.CanonicalPath,
            "More than one ready operation identifies the same logical source.",
            policy,
            problems);
        AddDuplicateProblems(
            readyItems,
            item => item.ProposedTargetPath!,
            "More than one ready operation identifies the same logical target.",
            policy,
            problems);
    }

    private static void AddDuplicateProblems(
        IEnumerable<OrganizationPlanItem> readyItems,
        Func<OrganizationPlanItem, CanonicalPath> pathSelector,
        string description,
        PathPolicy policy,
        List<ExecutionManifestProblem> problems)
    {
        IEnumerable<IGrouping<CanonicalPath, OrganizationPlanItem>> duplicateGroups = readyItems
            .GroupBy(pathSelector, policy.LogicalComparer)
            .Where(group => group.Skip(1).Any());

        foreach (IGrouping<CanonicalPath, OrganizationPlanItem> group in duplicateGroups)
        {
            foreach (OrganizationPlanItem item in group)
            {
                AddInvalid(problems, item, description);
            }
        }
    }

    private static void AddInvalid(
        List<ExecutionManifestProblem> problems,
        OrganizationPlanItem item,
        string description) =>
        problems.Add(
            new ExecutionManifestProblem(
                ExecutionManifestProblemKind.InvalidReadyItem,
                description,
                item.SourcePrecondition.CanonicalPath,
                item.ProposedAction?.Kind));

    private sealed class ReadyItemComparer(PathPolicy pathPolicy) : IComparer<OrganizationPlanItem>
    {
        public int Compare(OrganizationPlanItem? left, OrganizationPlanItem? right)
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

            int source = pathPolicy.DeterministicComparer.Compare(
                left.SourcePrecondition.CanonicalPath,
                right.SourcePrecondition.CanonicalPath);
            if (source != 0)
            {
                return source;
            }

            int target = pathPolicy.DeterministicComparer.Compare(
                left.ProposedTargetPath,
                right.ProposedTargetPath);
            if (target != 0)
            {
                return target;
            }

            int ruleOrder = left.SelectedRule!.Rule.Order.CompareTo(right.SelectedRule!.Rule.Order);
            return ruleOrder != 0
                ? ruleOrder
                : StringComparer.Ordinal.Compare(
                    left.SelectedRule.Rule.Name,
                    right.SelectedRule.Rule.Name);
        }
    }
}
