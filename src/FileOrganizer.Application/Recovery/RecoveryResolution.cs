namespace FileOrganizer.Application.Recovery;

/// <summary>An immutable classification of one pending move intent.</summary>
public sealed class RecoveryResolution
{
    internal RecoveryResolution(
        RecoveryDisposition disposition,
        RecoveryReasonKind reason)
    {
        Disposition = disposition;
        Reason = reason;
    }

    public RecoveryDisposition Disposition { get; }

    public RecoveryReasonKind Reason { get; }
}
