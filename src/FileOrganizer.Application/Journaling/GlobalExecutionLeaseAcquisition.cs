namespace FileOrganizer.Application.Journaling;

/// <summary>
/// An immutable lease-acquisition result. Only <see cref="Acquired"/> grants
/// authority to enter an execution or physical-recovery boundary.
/// </summary>
public sealed class GlobalExecutionLeaseAcquisition
{
    private GlobalExecutionLeaseAcquisition(
        GlobalExecutionLeaseAcquisitionStatus status,
        IGlobalExecutionLease? lease,
        bool previousOwnerAbandoned,
        GlobalExecutionLeaseFailureKind? failureKind)
    {
        bool acquired = status == GlobalExecutionLeaseAcquisitionStatus.Acquired;
        if (acquired != (lease is not null))
        {
            throw new ArgumentException(
                "Only an acquired result may expose a lease capability.",
                nameof(lease));
        }

        if (previousOwnerAbandoned && !acquired)
        {
            throw new ArgumentException(
                "Abandoned-owner metadata requires an acquired lease.",
                nameof(previousOwnerAbandoned));
        }

        bool unsupported = status == GlobalExecutionLeaseAcquisitionStatus.Unsupported;
        bool failed = status == GlobalExecutionLeaseAcquisitionStatus.Failed;
        if (unsupported != (failureKind == GlobalExecutionLeaseFailureKind.UnsupportedPlatform)
            || failed != (failureKind is
                GlobalExecutionLeaseFailureKind.AccessDenied
                or GlobalExecutionLeaseFailureKind.SynchronizationFailure))
        {
            throw new ArgumentException(
                "Unsupported and failed acquisitions require a matching failure kind.",
                nameof(failureKind));
        }

        Status = status;
        Lease = lease;
        PreviousOwnerAbandoned = previousOwnerAbandoned;
        FailureKind = failureKind;
    }

    public GlobalExecutionLeaseAcquisitionStatus Status { get; }

    /// <summary>
    /// Gets the exclusive capability. It remains authoritative until disposed
    /// and is present only when <see cref="Status"/> is Acquired.
    /// </summary>
    public IGlobalExecutionLease? Lease { get; }

    /// <summary>
    /// Gets whether the OS reported that a prior owner terminated without
    /// releasing ownership. This does not establish journal or user-file safety.
    /// </summary>
    public bool PreviousOwnerAbandoned { get; }

    public GlobalExecutionLeaseFailureKind? FailureKind { get; }

    public static GlobalExecutionLeaseAcquisition Acquired(
        IGlobalExecutionLease lease,
        bool previousOwnerAbandoned = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new(
            GlobalExecutionLeaseAcquisitionStatus.Acquired,
            lease,
            previousOwnerAbandoned,
            failureKind: null);
    }

    public static GlobalExecutionLeaseAcquisition Busy() => new(
        GlobalExecutionLeaseAcquisitionStatus.Busy,
        lease: null,
        previousOwnerAbandoned: false,
        failureKind: null);

    public static GlobalExecutionLeaseAcquisition Unsupported() => new(
        GlobalExecutionLeaseAcquisitionStatus.Unsupported,
        lease: null,
        previousOwnerAbandoned: false,
        GlobalExecutionLeaseFailureKind.UnsupportedPlatform);

    public static GlobalExecutionLeaseAcquisition Failed(
        GlobalExecutionLeaseFailureKind failureKind)
    {
        if (failureKind == GlobalExecutionLeaseFailureKind.UnsupportedPlatform)
        {
            throw new ArgumentException(
                "Use Unsupported for an unavailable platform capability.",
                nameof(failureKind));
        }

        return new(
            GlobalExecutionLeaseAcquisitionStatus.Failed,
            lease: null,
            previousOwnerAbandoned: false,
            failureKind);
    }
}
