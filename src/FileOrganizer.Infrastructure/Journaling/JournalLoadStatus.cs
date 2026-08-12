namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>Distinguishes absence from a trusted load and an invalid durable journal.</summary>
public enum JournalLoadStatus
{
    NotFound,
    Loaded,
    Invalid,
}
