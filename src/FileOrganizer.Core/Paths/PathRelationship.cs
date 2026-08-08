namespace FileOrganizer.Core.Paths;

/// <summary>The lexical relationship between a declared root and another path.</summary>
public enum PathRelationship
{
    /// <summary>The two paths name the same logical path.</summary>
    Same = 0,

    /// <summary>The candidate is below the root on a component boundary.</summary>
    Descendant = 1,

    /// <summary>The paths share a filesystem root, but the candidate is not contained.</summary>
    Outside = 2,

    /// <summary>The paths use different drive, share, or POSIX roots.</summary>
    DifferentRoot = 3,

    /// <summary>The paths were produced under incompatible lexical semantics.</summary>
    PolicyMismatch = 4,
}
