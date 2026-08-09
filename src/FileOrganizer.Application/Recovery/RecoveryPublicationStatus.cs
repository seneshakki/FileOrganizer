namespace FileOrganizer.Application.Recovery;

/// <summary>Whether recovery requires a new durable journal publication.</summary>
public enum RecoveryPublicationStatus
{
    PublicationRequired,
    AlreadyRecorded,
}
