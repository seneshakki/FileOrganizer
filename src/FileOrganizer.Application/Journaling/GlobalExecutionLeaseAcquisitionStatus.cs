namespace FileOrganizer.Application.Journaling;

/// <summary>The structured outcome of one global execution-lease acquisition.</summary>
public enum GlobalExecutionLeaseAcquisitionStatus
{
    /// <summary>The caller exclusively owns the returned lease capability.</summary>
    Acquired,

    /// <summary>Another execution or recovery owner currently holds the lease.</summary>
    Busy,

    /// <summary>The required operating-system capability is unavailable.</summary>
    Unsupported,

    /// <summary>The operating system could not complete lease acquisition safely.</summary>
    Failed,
}
