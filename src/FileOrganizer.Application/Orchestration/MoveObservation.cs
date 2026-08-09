using FileOrganizer.Application.Recovery;

namespace FileOrganizer.Application.Orchestration;

/// <summary>An explicit source/target observation made after an intent or during recovery.</summary>
public sealed class MoveObservation
{
    public MoveObservation(
        RecoveryEntryObservation source,
        RecoveryEntryObservation target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        Source = source;
        Target = target;
    }

    public RecoveryEntryObservation Source { get; }

    public RecoveryEntryObservation Target { get; }
}
