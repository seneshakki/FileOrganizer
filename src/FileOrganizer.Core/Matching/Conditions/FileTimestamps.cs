using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

internal static class FileTimestamps
{
    /// <summary>
    /// The reason a condition about creation time cannot be answered for a file
    /// whose creation time was never reported.
    /// </summary>
    public static readonly EvaluationProblem CreationTimeUnavailable = new(
        EvaluationProblemKind.MetadataUnavailable,
        "This file system did not report when the file was created, so a rule about "
        + "its creation date cannot be decided. Use the modification date instead.");

    public static DateTimeOffset? Read(TimestampField field, FileEntry file) => field switch
    {
        TimestampField.Modified => file.ModifiedUtc,
        TimestampField.Created => file.CreatedUtc,

        // Unreachable: an unrecognised field is rejected at construction.
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
