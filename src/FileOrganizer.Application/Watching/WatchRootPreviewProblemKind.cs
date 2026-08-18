namespace FileOrganizer.Application.Watching;

/// <summary>A stable classification for a watched-root preview problem.</summary>
public enum WatchRootPreviewProblemKind
{
    StaleSnapshot = 0,
    IncompleteSourceScan,
    MissingSourceRootIdentity,
    SourceRootIdentityChanged,
    InvalidCandidate,
    CandidateMissing,
    CandidateChanged,
    SourceScanFailed,
    PlanningFailed,
    DestinationInspectionFailed,
}
