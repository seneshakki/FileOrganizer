using System.Collections.ObjectModel;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// Immutable output from pure rule evaluation and target resolution, ready for
/// an explicit read-only destination inspection.
/// </summary>
public sealed class OrganizationPlanPreparation
{
    private readonly ReadOnlyCollection<CanonicalPath> _destinationPaths;
    private readonly ReadOnlyCollection<CanonicalPath> _destinationDirectories;
    private readonly ReadOnlyCollection<PreparedPlanItem> _items;
    private readonly ReadOnlyCollection<CanonicalPath> _proposedTargetPaths;
    private readonly ReadOnlyCollection<ScanDiagnostic> _scanDiagnostics;

    internal OrganizationPlanPreparation(
        DateTimeOffset generatedAtUtc,
        CanonicalPath sourceRoot,
        bool isScanComplete,
        IEnumerable<ScanDiagnostic> scanDiagnostics,
        PathPolicy pathPolicy,
        IEnumerable<PreparedPlanItem> items,
        IEnumerable<CanonicalPath> destinationDirectories,
        IEnumerable<CanonicalPath> proposedTargetPaths,
        IEnumerable<CanonicalPath> destinationPaths)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(scanDiagnostics);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(destinationDirectories);
        ArgumentNullException.ThrowIfNull(proposedTargetPaths);
        ArgumentNullException.ThrowIfNull(destinationPaths);

        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        SourceRoot = sourceRoot;
        IsScanComplete = isScanComplete;
        IsSourceRootCompatible = sourceRoot.Semantics == pathPolicy.Semantics;
        PathSemantics = pathPolicy.Semantics;
        PathPolicy = pathPolicy;
        _scanDiagnostics = Snapshot(scanDiagnostics, nameof(scanDiagnostics));
        _items = Snapshot(items, nameof(items));
        _destinationDirectories = Snapshot(
            destinationDirectories,
            nameof(destinationDirectories));
        _proposedTargetPaths = Snapshot(proposedTargetPaths, nameof(proposedTargetPaths));
        _destinationPaths = Snapshot(destinationPaths, nameof(destinationPaths));
    }

    /// <summary>Gets the single logical instant used for this preparation.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Gets the canonical root declared by the source scan.</summary>
    public CanonicalPath SourceRoot { get; }

    /// <summary>Gets whether the source scan accounted for its whole scope.</summary>
    public bool IsScanComplete { get; }

    /// <summary>
    /// Gets whether the source root was canonicalized under the same explicit
    /// path semantics used by this preparation.
    /// </summary>
    public bool IsSourceRootCompatible { get; }

    /// <summary>Gets the source scan diagnostics.</summary>
    public IReadOnlyList<ScanDiagnostic> ScanDiagnostics => _scanDiagnostics;

    /// <summary>Gets the explicit lexical semantics used to resolve candidates.</summary>
    public PathSemantics PathSemantics { get; }

    /// <summary>
    /// Gets every unique logical path that Infrastructure must inspect without
    /// modifying it, including required directories and proposed file targets.
    /// </summary>
    public IReadOnlyList<CanonicalPath> DestinationPaths => _destinationPaths;

    /// <summary>Gets the unique directories that must already exist as directories.</summary>
    public IReadOnlyList<CanonicalPath> DestinationDirectories => _destinationDirectories;

    /// <summary>Gets the unique proposed file targets.</summary>
    public IReadOnlyList<CanonicalPath> ProposedTargetPaths => _proposedTargetPaths;

    internal PathPolicy PathPolicy { get; }

    internal IReadOnlyList<PreparedPlanItem> Items => _items;

    private static ReadOnlyCollection<T> Snapshot<T>(
        IEnumerable<T> source,
        string parameterName)
        where T : class
    {
        T[] materialized = [.. source];
        foreach (T item in materialized)
        {
            ArgumentNullException.ThrowIfNull(item, parameterName);
        }

        return Array.AsReadOnly(materialized);
    }
}
