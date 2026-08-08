namespace FileOrganizer.Core.Paths;

/// <summary>Why a lexical path operation could not produce a safe result.</summary>
public enum PathProblemKind
{
    /// <summary>The path or file name was null or empty.</summary>
    Empty = 0,

    /// <summary>The input contains a null character, which no supported file system accepts.</summary>
    ContainsNull = 1,

    /// <summary>The input is relative or only partially qualified.</summary>
    NotFullyQualified = 2,

    /// <summary>The root is malformed, such as a UNC path without a share.</summary>
    MalformedRoot = 3,

    /// <summary>The input uses a path form this milestone deliberately does not support.</summary>
    UnsupportedPathForm = 4,

    /// <summary>A path component is not valid under the selected lexical policy.</summary>
    InvalidSegment = 5,

    /// <summary>Resolving parent segments would move above the path root.</summary>
    TraversesAboveRoot = 6,

    /// <summary>The proposed file name is not one literal path component.</summary>
    InvalidSingleSegment = 7,

    /// <summary>The selected Unicode policy could not normalize malformed UTF-16 input.</summary>
    InvalidUnicodeForNormalization = 8,

    /// <summary>The path was produced under different lexical semantics.</summary>
    PolicyMismatch = 9,
}
