namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Which part of a file's name a condition looks at.
/// </summary>
public enum NameTarget
{
    /// <summary>The whole name, extension included.</summary>
    FullName = 0,

    /// <summary>The name with its extension removed.</summary>
    WithoutExtension = 1,
}
