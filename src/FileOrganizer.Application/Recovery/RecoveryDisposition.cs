namespace FileOrganizer.Application.Recovery;

/// <summary>The safety decision derived from source and target observations.</summary>
public enum RecoveryDisposition
{
    Applied,
    NotApplied,
    Conflict,
    Ambiguous,
    ManualRecovery,
}
