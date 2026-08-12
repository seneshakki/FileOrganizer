namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>A structured durable-journal validation diagnostic.</summary>
public sealed record JournalLoadProblem(
    JournalLoadProblemKind Kind,
    string Description,
    long? RecordSequence = null,
    string? FileName = null);
