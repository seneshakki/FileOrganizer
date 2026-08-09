using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Recovery;

/// <summary>An immutable, explicit observation supplied to pure recovery logic.</summary>
public sealed class RecoveryEntryObservation
{
    private RecoveryEntryObservation(
        RecoveryEntryObservationKind kind,
        EntryIdentity? identity,
        RecoveryInspectionFailureKind? failureKind)
    {
        Kind = kind;
        Identity = identity;
        FailureKind = failureKind;
    }

    public RecoveryEntryObservationKind Kind { get; }

    public EntryIdentity? Identity { get; }

    public RecoveryInspectionFailureKind? FailureKind { get; }

    public static RecoveryEntryObservation Missing() =>
        new(RecoveryEntryObservationKind.Missing, null, null);

    public static RecoveryEntryObservation Present(EntryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(RecoveryEntryObservationKind.Present, identity, null);
    }

    public static RecoveryEntryObservation InspectionFailed(
        RecoveryInspectionFailureKind failureKind)
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        return new(
            RecoveryEntryObservationKind.InspectionFailed,
            null,
            failureKind);
    }
}
