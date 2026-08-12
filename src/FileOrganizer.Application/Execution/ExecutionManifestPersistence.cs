using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Execution;

/// <summary>Validates and restores persisted execution-manifest values without I/O.</summary>
public static class ExecutionManifestPersistence
{
    public static ExecutionManifest RestoreManifest(
        PlanFingerprint expectedFingerprint,
        DateTimeOffset generatedAtUtc,
        PathSemantics pathSemantics,
        string sourceRoot,
        PhysicalEntryIdentity sourceRootPhysicalIdentity,
        IEnumerable<ExecutionManifestPersistenceItem> items)
    {
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(sourceRootPhysicalIdentity);
        ArgumentNullException.ThrowIfNull(items);

        if (sourceRootPhysicalIdentity.EntryKind != PhysicalEntryKind.Directory)
        {
            throw new ArgumentException(
                "The persisted source-root identity must identify a directory.",
                nameof(sourceRootPhysicalIdentity));
        }

        PathPolicy policy = new(
            pathSemantics.Syntax,
            pathSemantics.CaseSensitivity,
            pathSemantics.UnicodeNormalization);
        CanonicalPath canonicalSourceRoot = RequirePath(
            policy,
            sourceRoot,
            nameof(sourceRoot));

        ExecutionManifestPersistenceItem[] itemSnapshot = [.. items];
        foreach (ExecutionManifestPersistenceItem item in itemSnapshot)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }

        Array.Sort(itemSnapshot, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        ExecutionManifestItem[] restoredItems = new ExecutionManifestItem[itemSnapshot.Length];
        for (int index = 0; index < itemSnapshot.Length; index++)
        {
            ExecutionManifestPersistenceItem item = itemSnapshot[index];
            if (item.Ordinal != index)
            {
                throw new ArgumentException(
                    "Persisted operation ordinals must be unique and contiguous from zero.",
                    nameof(items));
            }

            if (item.ActionKind != OrganizationActionKind.MoveToDirectory)
            {
                throw new ArgumentException(
                    "Only move-to-directory persisted actions are supported.",
                    nameof(items));
            }

            CanonicalPath source = RequirePath(policy, item.SourcePath, nameof(items));
            CanonicalPath destination = RequirePath(
                policy,
                item.DestinationDirectory,
                nameof(items));
            CanonicalPath target = RequirePath(policy, item.TargetPath, nameof(items));
            ValidateRelationships(
                policy,
                canonicalSourceRoot,
                source,
                destination,
                target,
                nameof(items));

            SourcePrecondition precondition = new(
                source,
                item.SourceLength,
                item.SourceCreatedUtc,
                item.SourceModifiedUtc,
                item.SourcePhysicalIdentity);
            restoredItems[index] = new ExecutionManifestItem(
                item.Ordinal,
                precondition,
                destination,
                item.DestinationDirectoryPhysicalIdentity,
                target,
                item.SelectedRuleName,
                item.SelectedRuleOrder);
        }

        RejectLogicalDuplicates(restoredItems, policy, nameof(items));

        PlanFingerprint actualFingerprint = ExecutionManifestFingerprint.Compute(
            canonicalSourceRoot,
            sourceRootPhysicalIdentity,
            restoredItems,
            policy);
        if (!actualFingerprint.Equals(expectedFingerprint))
        {
            throw new ArgumentException(
                "The persisted manifest does not match its plan fingerprint.",
                nameof(expectedFingerprint));
        }

        return new ExecutionManifest(
            actualFingerprint,
            generatedAtUtc,
            canonicalSourceRoot,
            sourceRootPhysicalIdentity,
            restoredItems);
    }

    private static CanonicalPath RequirePath(
        PathPolicy policy,
        string value,
        string parameterName)
    {
        PathResolution resolution = policy.CanonicalizeAbsolute(value);
        if (!resolution.Succeeded)
        {
            throw new ArgumentException(
                $"A persisted manifest path is invalid: {resolution.Problem!.Kind}.",
                parameterName);
        }

        return resolution.Path!;
    }

    private static void ValidateRelationships(
        PathPolicy policy,
        CanonicalPath sourceRoot,
        CanonicalPath source,
        CanonicalPath destination,
        CanonicalPath target,
        string parameterName)
    {
        if (policy.Relate(sourceRoot, source) != PathRelationship.Descendant)
        {
            throw new ArgumentException(
                "A persisted source must be strictly contained by the source root.",
                parameterName);
        }

        if (policy.Relate(destination, target) != PathRelationship.Descendant)
        {
            throw new ArgumentException(
                "A persisted target must be strictly contained by its destination directory.",
                parameterName);
        }

        if (policy.AreEquivalent(source, target))
        {
            throw new ArgumentException(
                "A persisted source and target cannot identify the same logical path.",
                parameterName);
        }

        PathResolution expectedTarget = policy.AppendFileName(destination, source.FileName);
        if (!expectedTarget.Succeeded || !policy.AreEquivalent(expectedTarget.Path!, target))
        {
            throw new ArgumentException(
                "A persisted move target must preserve the source file name.",
                parameterName);
        }
    }

    private static void RejectLogicalDuplicates(
        IEnumerable<ExecutionManifestItem> items,
        PathPolicy policy,
        string parameterName)
    {
        HashSet<CanonicalPath> sources = new(policy.LogicalComparer);
        HashSet<CanonicalPath> targets = new(policy.LogicalComparer);
        foreach (ExecutionManifestItem item in items)
        {
            if (!sources.Add(item.SourcePrecondition.CanonicalPath)
                || !targets.Add(item.TargetPath))
            {
                throw new ArgumentException(
                    "Persisted manifest sources and targets must be logically unique.",
                    parameterName);
            }
        }
    }
}
