using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Planning;

/// <summary>An immutable read-only observation about one proposed target path.</summary>
public sealed record DestinationPathFact
{
    public DestinationPathFact(
        CanonicalPath path,
        DestinationEntryKind entryKind,
        DestinationInspectionFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!Enum.IsDefined(entryKind))
        {
            throw new ArgumentOutOfRangeException(nameof(entryKind));
        }

        bool failed = entryKind == DestinationEntryKind.InspectionFailed;
        if (failed != (failure is not null))
        {
            throw new ArgumentException(
                "An inspection failure is required exactly when inspection failed.",
                nameof(failure));
        }

        Path = path;
        EntryKind = entryKind;
        Failure = failure;
    }

    /// <summary>Gets the canonical path that was inspected.</summary>
    public CanonicalPath Path { get; }

    /// <summary>Gets what the read-only inspection found.</summary>
    public DestinationEntryKind EntryKind { get; }

    /// <summary>Gets why inspection failed, if it did.</summary>
    public DestinationInspectionFailure? Failure { get; }
}
