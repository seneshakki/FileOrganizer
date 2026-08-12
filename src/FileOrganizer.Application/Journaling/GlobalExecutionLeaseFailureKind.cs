namespace FileOrganizer.Application.Journaling;

/// <summary>A stable reason why a global execution lease was unavailable.</summary>
public enum GlobalExecutionLeaseFailureKind
{
    /// <summary>The current platform cannot provide the required capability.</summary>
    UnsupportedPlatform,

    /// <summary>The operating system denied access to the synchronization object.</summary>
    AccessDenied,

    /// <summary>The synchronization primitive failed for another reason.</summary>
    SynchronizationFailure,
}
