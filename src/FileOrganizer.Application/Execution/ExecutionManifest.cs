using System.Collections.ObjectModel;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Execution;

/// <summary>
/// An immutable, deterministic list of move operations approved for execution orchestration.
/// </summary>
/// <remarks>
/// A manifest is still only a request. An executor must revalidate every source and
/// destination immediately before any physical operation.
/// </remarks>
public sealed class ExecutionManifest
{
    /// <summary>The canonical binary fingerprint format emitted by this version.</summary>
    public const int FingerprintFormatVersion = 1;

    private readonly ReadOnlyCollection<ExecutionManifestItem> _items;

    internal ExecutionManifest(
        PlanFingerprint fingerprint,
        DateTimeOffset generatedAtUtc,
        CanonicalPath sourceRoot,
        IEnumerable<ExecutionManifestItem> items)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(items);

        ExecutionManifestItem[] snapshot = [.. items];
        for (int index = 0; index < snapshot.Length; index++)
        {
            ExecutionManifestItem item = snapshot[index]
                ?? throw new ArgumentException("Manifest items cannot contain null.", nameof(items));

            if (item.Ordinal != index)
            {
                throw new ArgumentException(
                    "Manifest item ordinals must be contiguous and match their deterministic order.",
                    nameof(items));
            }

            if (item.SourcePrecondition.CanonicalPath.Semantics != sourceRoot.Semantics)
            {
                throw new ArgumentException(
                    "Every manifest item must use the source root's path semantics.",
                    nameof(items));
            }
        }

        Fingerprint = fingerprint;
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        SourceRoot = sourceRoot;
        _items = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the stable SHA-256 identity of all execution-critical fields.</summary>
    public PlanFingerprint Fingerprint { get; }

    /// <summary>Gets the UTC generation time inherited from the dry-run plan.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Gets the canonical source root inherited from the dry-run plan.</summary>
    public CanonicalPath SourceRoot { get; }

    /// <summary>Gets the explicit lexical semantics shared by all manifest paths.</summary>
    public PathSemantics PathSemantics => SourceRoot.Semantics;

    /// <summary>Gets move operations in deterministic execution order.</summary>
    public IReadOnlyList<ExecutionManifestItem> Items => _items;

    /// <summary>Gets whether the eligible plan contained no ready operations.</summary>
    public bool IsEmpty => _items.Count == 0;
}
