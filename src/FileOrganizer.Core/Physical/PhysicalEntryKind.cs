namespace FileOrganizer.Core.Physical;

/// <summary>The physical kind observed for one file-system entry.</summary>
public enum PhysicalEntryKind
{
    /// <summary>An ordinary data file.</summary>
    RegularFile = 0,

    /// <summary>An ordinary directory.</summary>
    Directory,

    /// <summary>A symbolic link, junction, or another reparse-point entry.</summary>
    ReparsePoint,

    /// <summary>An entry kind that this application cannot safely organize.</summary>
    Unsupported,
}
