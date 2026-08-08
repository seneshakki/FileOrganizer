namespace FileOrganizer.Core.Paths;

/// <summary>
/// The lexical grammar used to understand a path.
/// </summary>
public enum PathSyntax
{
    /// <summary>Drive-letter and UNC paths with Windows separators.</summary>
    Windows = 0,

    /// <summary>Slash-rooted POSIX paths.</summary>
    Posix = 1,
}
