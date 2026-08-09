using FileOrganizer.Core.Planning;

namespace FileOrganizer.Core.Abstractions;

/// <summary>
/// Captures explicit, read-only facts for every path requested by a prepared
/// organization plan.
/// </summary>
/// <remarks>
/// The planner receives the returned snapshot as data; it never performs
/// hidden live file-system reads. Implementations expose no mutation methods.
/// Cancellation propagates and must not be converted into a destination fact.
/// </remarks>
public interface IDestinationStateInspector
{
    /// <summary>Inspects exactly the paths requested by a preparation.</summary>
    DestinationStateSnapshot Inspect(
        OrganizationPlanPreparation preparation,
        CancellationToken cancellationToken = default);
}
