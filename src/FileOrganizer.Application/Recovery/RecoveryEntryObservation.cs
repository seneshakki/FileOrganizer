using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Recovery;

/// <summary>An immutable, explicit observation supplied to pure recovery logic.</summary>
public sealed class RecoveryEntryObservation
{
    private RecoveryEntryObservation(
        RecoveryEntryObservationKind kind,
        PhysicalEntryIdentity? identity,
        RecoveryInspectionFailureKind? failureKind)
    {
        Kind = kind;
        Identity = identity;
        FailureKind = failureKind;
    }

    public RecoveryEntryObservationKind Kind { get; }

    public PhysicalEntryIdentity? Identity { get; }

    public RecoveryInspectionFailureKind? FailureKind { get; }

    public static RecoveryEntryObservation Missing() =>
        new(RecoveryEntryObservationKind.Missing, null, null);

    public static RecoveryEntryObservation Present(PhysicalEntryIdentity identity)
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
