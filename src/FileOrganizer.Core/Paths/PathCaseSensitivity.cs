namespace FileOrganizer.Core.Paths;

/// <summary>
/// Whether two path components that differ only by case name the same logical path.
/// </summary>
public enum PathCaseSensitivity
{
    /// <summary>Case is significant and comparisons are ordinal.</summary>
    Sensitive = 0,

    /// <summary>Case is ignored using an ordinal, culture-independent comparison.</summary>
    Insensitive = 1,
}
