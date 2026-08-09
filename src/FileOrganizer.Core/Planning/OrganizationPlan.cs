using System.Collections.ObjectModel;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// An immutable dry-run plan containing no capability to modify the file system.
/// </summary>
public sealed class OrganizationPlan
{
    private readonly ReadOnlyCollection<OrganizationPlanDiagnostic> _diagnostics;
    private readonly ReadOnlyCollection<OrganizationPlanItem> _items;
    private readonly ReadOnlyCollection<ScanDiagnostic> _scanDiagnostics;

    internal OrganizationPlan(
        DateTimeOffset generatedAtUtc,
        CanonicalPath sourceRoot,
        bool isScanComplete,
        IEnumerable<ScanDiagnostic> scanDiagnostics,
        IEnumerable<OrganizationPlanDiagnostic> diagnostics,
        IEnumerable<OrganizationPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(scanDiagnostics);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(items);

        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        SourceRoot = sourceRoot;
        IsScanComplete = isScanComplete;
        _scanDiagnostics = Snapshot(scanDiagnostics, nameof(scanDiagnostics));
        _diagnostics = Snapshot(diagnostics, nameof(diagnostics));
        _items = Snapshot(items, nameof(items));

        IsEligibleForExecution = IsScanComplete
            && !_items.Any(item => item.Outcome is PlanItemOutcome.Blocked or PlanItemOutcome.Conflict)
            && _diagnostics.Count == 0;
    }

    /// <summary>
    /// Gets the single logical instant used for every time-dependent rule and
    /// recorded as this plan's generation time.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Gets the canonical source root declared by the scan.</summary>
    public CanonicalPath SourceRoot { get; }

    /// <summary>Gets whether the scanner accounted for its complete scope.</summary>
    public bool IsScanComplete { get; }

    /// <summary>Gets structured diagnostics copied from the source scan.</summary>
    public IReadOnlyList<ScanDiagnostic> ScanDiagnostics => _scanDiagnostics;

    /// <summary>Gets diagnostics applying to the plan as a whole.</summary>
    public IReadOnlyList<OrganizationPlanDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Gets items in deterministic source-path order.</summary>
    public IReadOnlyList<OrganizationPlanItem> Items => _items;

    /// <summary>
    /// Gets whether this complete snapshot contains no blocked or conflicting
    /// proposal. A future executor must still revalidate all preconditions.
    /// </summary>
    public bool IsEligibleForExecution { get; }

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
