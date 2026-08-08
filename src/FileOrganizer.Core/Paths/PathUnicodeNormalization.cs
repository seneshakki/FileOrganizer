namespace FileOrganizer.Core.Paths;

/// <summary>
/// Whether canonically equivalent Unicode spellings name the same logical path.
/// </summary>
public enum PathUnicodeNormalization
{
    /// <summary>Compare the UTF-16 spelling exactly, without Unicode normalization.</summary>
    None = 0,

    /// <summary>Compare Unicode normalization form C keys.</summary>
    FormC = 1,
}
