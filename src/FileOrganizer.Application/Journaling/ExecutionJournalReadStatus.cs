namespace FileOrganizer.Application.Journaling;

/// <summary>Distinguishes journal absence from trusted and invalid durable input.</summary>
public enum ExecutionJournalReadStatus
{
    NotFound,
    Loaded,
    Invalid,
}
