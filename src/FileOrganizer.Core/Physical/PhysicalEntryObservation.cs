namespace FileOrganizer.Core.Physical;

/// <summary>
/// Immutable output from one read-only, handle-based physical observation.
/// </summary>
public sealed class PhysicalEntryObservation
{
    private PhysicalEntryObservation(
        PhysicalEntryObservationOutcome outcome,
        PhysicalEntryObservationReason reason,
        PhysicalEntryKind? entryKind,
        PhysicalEntryIdentity? identity,
        PhysicalFileMetadata? fileMetadata,
        int? nativeErrorCode)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (entryKind.HasValue && !Enum.IsDefined(entryKind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(entryKind));
        }

        if (nativeErrorCode.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nativeErrorCode.Value);
        }

        bool succeeded = outcome == PhysicalEntryObservationOutcome.Success;
        if (succeeded != (identity is not null)
            || succeeded != (reason == PhysicalEntryObservationReason.None))
        {
            throw new ArgumentException(
                "Success requires identity and no failure reason; failures require no identity and a reason.",
                nameof(outcome));
        }

        if (succeeded && nativeErrorCode.HasValue)
        {
            throw new ArgumentException(
                "A successful observation cannot contain a native error code.",
                nameof(nativeErrorCode));
        }

        if (identity is not null
            && (!entryKind.HasValue || identity.EntryKind != entryKind.Value))
        {
            throw new ArgumentException(
                "The observed kind must agree with the physical identity.",
                nameof(entryKind));
        }

        if (fileMetadata is not null && entryKind != PhysicalEntryKind.RegularFile)
        {
            throw new ArgumentException(
                "File metadata can be retained only for an observed regular file.",
                nameof(fileMetadata));
        }

        if (succeeded
            && ((entryKind == PhysicalEntryKind.RegularFile) != (fileMetadata is not null)))
        {
            throw new ArgumentException(
                "A successful regular-file observation requires metadata; a directory does not have file metadata.",
                nameof(fileMetadata));
        }

        ValidateFailureShape(outcome, reason, entryKind);

        Outcome = outcome;
        Reason = reason;
        EntryKind = entryKind;
        Identity = identity;
        FileMetadata = fileMetadata;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Gets the high-level observation outcome.</summary>
    public PhysicalEntryObservationOutcome Outcome { get; }

    /// <summary>Gets the exact structured explanation for the outcome.</summary>
    public PhysicalEntryObservationReason Reason { get; }

    /// <summary>Gets the physical kind when it could be established.</summary>
    public PhysicalEntryKind? EntryKind { get; }

    /// <summary>Gets strong identity only after a fully successful observation.</summary>
    public PhysicalEntryIdentity? Identity { get; }

    /// <summary>
    /// Gets file metadata captured from the same handle. A failed identity
    /// query may retain this metadata for preview-only use.
    /// </summary>
    public PhysicalFileMetadata? FileMetadata { get; }

    /// <summary>Gets an optional platform error code retained for diagnostics.</summary>
    public int? NativeErrorCode { get; }

    /// <summary>Creates a complete ordinary-file or directory observation.</summary>
    public static PhysicalEntryObservation Success(
        PhysicalEntryIdentity identity,
        PhysicalFileMetadata? fileMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new(
            PhysicalEntryObservationOutcome.Success,
            PhysicalEntryObservationReason.None,
            identity.EntryKind,
            identity,
            fileMetadata,
            null);
    }

    /// <summary>Creates a structured missing-entry observation.</summary>
    public static PhysicalEntryObservation Missing(int? nativeErrorCode = null) =>
        new(
            PhysicalEntryObservationOutcome.Missing,
            PhysicalEntryObservationReason.EntryMissing,
            null,
            null,
            null,
            nativeErrorCode);

    /// <summary>Creates a structured inaccessible-entry observation.</summary>
    public static PhysicalEntryObservation Inaccessible(
        PhysicalEntryObservationReason reason,
        int? nativeErrorCode = null,
        PhysicalEntryKind? entryKind = null,
        PhysicalFileMetadata? fileMetadata = null) =>
        new(
            PhysicalEntryObservationOutcome.Inaccessible,
            reason,
            entryKind,
            null,
            fileMetadata,
            nativeErrorCode);

    /// <summary>Creates a structured unsupported-capability or entry observation.</summary>
    public static PhysicalEntryObservation Unsupported(
        PhysicalEntryObservationReason reason,
        PhysicalEntryKind? entryKind = null,
        PhysicalFileMetadata? fileMetadata = null,
        int? nativeErrorCode = null) =>
        new(
            PhysicalEntryObservationOutcome.Unsupported,
            reason,
            entryKind,
            null,
            fileMetadata,
            nativeErrorCode);

    private static void ValidateFailureShape(
        PhysicalEntryObservationOutcome outcome,
        PhysicalEntryObservationReason reason,
        PhysicalEntryKind? entryKind)
    {
        if (outcome == PhysicalEntryObservationOutcome.Success)
        {
            return;
        }

        bool validReason = outcome switch
        {
            PhysicalEntryObservationOutcome.Missing =>
                reason == PhysicalEntryObservationReason.EntryMissing,
            PhysicalEntryObservationOutcome.Inaccessible => reason is
                PhysicalEntryObservationReason.AccessDenied
                or PhysicalEntryObservationReason.SharingViolation
                or PhysicalEntryObservationReason.InputOutputFailure
                or PhysicalEntryObservationReason.EntryChanged,
            PhysicalEntryObservationOutcome.Unsupported => reason is
                PhysicalEntryObservationReason.UnsupportedCapability
                or PhysicalEntryObservationReason.ReparsePoint
                or PhysicalEntryObservationReason.UnsupportedEntryKind
                or PhysicalEntryObservationReason.DeletePending
                or PhysicalEntryObservationReason.InvalidPath
                or PhysicalEntryObservationReason.MetadataUnavailable,
            _ => false,
        };

        if (!validReason)
        {
            throw new ArgumentException(
                "The reason is inconsistent with the observation outcome.",
                nameof(reason));
        }

        if (reason == PhysicalEntryObservationReason.ReparsePoint
            && entryKind != PhysicalEntryKind.ReparsePoint)
        {
            throw new ArgumentException(
                "A reparse-point result must expose the reparse-point kind.",
                nameof(entryKind));
        }

        if (reason == PhysicalEntryObservationReason.UnsupportedEntryKind
            && entryKind != PhysicalEntryKind.Unsupported)
        {
            throw new ArgumentException(
                "An unsupported-entry result must expose the unsupported kind.",
                nameof(entryKind));
        }
    }
}
