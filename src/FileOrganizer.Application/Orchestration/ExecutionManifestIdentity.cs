using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Orchestration;

internal static class ExecutionManifestIdentity
{
    internal static bool AreExecutionCriticalFieldsEquivalent(
        ExecutionManifest left,
        ExecutionManifest right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!left.Fingerprint.Equals(right.Fingerprint)
            || left.PathSemantics != right.PathSemantics
            || !left.SourceRootPhysicalIdentity.Equals(right.SourceRootPhysicalIdentity)
            || left.Items.Count != right.Items.Count)
        {
            return false;
        }

        PathSemantics semantics = left.PathSemantics;
        PathPolicy policy = new(
            semantics.Syntax,
            semantics.CaseSensitivity,
            semantics.UnicodeNormalization);
        if (!policy.AreEquivalent(left.SourceRoot, right.SourceRoot))
        {
            return false;
        }

        for (int ordinal = 0; ordinal < left.Items.Count; ordinal++)
        {
            ExecutionManifestItem first = left.Items[ordinal];
            ExecutionManifestItem second = right.Items[ordinal];
            if (first.Ordinal != second.Ordinal
                || first.ActionKind != second.ActionKind
                || first.SourcePrecondition.Length != second.SourcePrecondition.Length
                || first.SourcePrecondition.CreatedUtc != second.SourcePrecondition.CreatedUtc
                || first.SourcePrecondition.ModifiedUtc != second.SourcePrecondition.ModifiedUtc
                || !first.SourcePrecondition.PhysicalIdentity!.Equals(
                    second.SourcePrecondition.PhysicalIdentity)
                || !first.DestinationDirectoryPhysicalIdentity.Equals(
                    second.DestinationDirectoryPhysicalIdentity)
                || !policy.AreEquivalent(
                    first.SourcePrecondition.CanonicalPath,
                    second.SourcePrecondition.CanonicalPath)
                || !policy.AreEquivalent(
                    first.DestinationDirectory,
                    second.DestinationDirectory)
                || !policy.AreEquivalent(first.TargetPath, second.TargetPath))
            {
                return false;
            }
        }

        return true;
    }
}
