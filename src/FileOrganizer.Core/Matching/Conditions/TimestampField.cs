namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Which of a file's timestamps a condition asks about.
/// </summary>
public enum TimestampField
{
    /// <summary>When the file was last written. Reported by every platform.</summary>
    Modified = 0,

    /// <summary>
    /// When the file was created. Not recorded everywhere; a condition about it
    /// resolves as undetermined for files whose creation time was not reported.
    /// </summary>
    Created = 1,
}
