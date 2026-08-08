namespace FileOrganizer.Core.Actions;

/// <summary>Which part of an organization action is not usable as configured.</summary>
public enum ActionValidationProblemKind
{
    /// <summary>A move or copy destination directory is not a supported absolute path.</summary>
    InvalidDestinationDirectory = 0,

    /// <summary>A rename target is not one valid literal file name.</summary>
    InvalidLiteralFileName = 1,
}
