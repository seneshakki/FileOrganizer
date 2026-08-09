namespace FileOrganizer.Core.Planning;

/// <summary>A stable, machine-readable explanation for a plan item outcome.</summary>
public enum PlanReasonKind
{
    /// <summary>No enabled, non-excluded rule matched the source.</summary>
    NoApplicableRule,

    /// <summary>A higher-priority rule could not reach a definite answer.</summary>
    RuleIndeterminate,

    /// <summary>The action selected for the source is not valid as configured.</summary>
    InvalidAction,

    /// <summary>The source path could not be interpreted by the selected path policy.</summary>
    InvalidSourcePath,

    /// <summary>The source is not contained by the declared scan root.</summary>
    SourceOutsideRoot,

    /// <summary>The proposed target path could not be constructed safely.</summary>
    InvalidTargetPath,

    /// <summary>The proposed target escaped the root that bounds the selected action.</summary>
    TargetOutsideRoot,

    /// <summary>The source and target denote the same logical path.</summary>
    SourceEqualsTarget,

    /// <summary>A file already exists at the proposed target.</summary>
    DestinationFileExists,

    /// <summary>A directory already exists where the proposed file would be placed.</summary>
    DestinationDirectoryExists,

    /// <summary>The destination could not be inspected safely.</summary>
    DestinationInspectionFailed,

    /// <summary>The supplied destination snapshot has no fact for this target.</summary>
    DestinationStateMissing,

    /// <summary>The directory required to contain the target does not exist.</summary>
    DestinationDirectoryMissing,

    /// <summary>A file exists where the action requires a destination directory.</summary>
    DestinationPathIsNotDirectory,

    /// <summary>Equivalent destination facts disagree and cannot safely be interpreted.</summary>
    AmbiguousDestinationState,

    /// <summary>At least two plan items propose the same logical target.</summary>
    DuplicateDestination,

    /// <summary>The destination snapshot uses different path semantics from the preparation.</summary>
    DestinationSnapshotPolicyMismatch,
}
