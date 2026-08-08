namespace FileOrganizer.Core.Actions;

/// <summary>The deliberately small set of organization intents M3A can plan.</summary>
public enum OrganizationActionKind
{
    /// <summary>Plan moving a file into a configured directory.</summary>
    MoveToDirectory = 0,

    /// <summary>Plan copying a file into a configured directory.</summary>
    CopyToDirectory = 1,

    /// <summary>Plan renaming a file to one literal name in its current directory.</summary>
    RenameToLiteralFileName = 2,
}
