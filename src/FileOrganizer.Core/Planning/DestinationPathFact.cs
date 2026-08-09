using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Planning;

/// <summary>An immutable read-only observation about one proposed target path.</summary>
public sealed record DestinationPathFact
{
    public DestinationPathFact(
        CanonicalPath path,
        DestinationEntryKind entryKind,
        DestinationInspectionFailure? failure = null,
        PhysicalEntryIdentity? physicalIdentity = null)
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

        if (entryKind is DestinationEntryKind.Missing or DestinationEntryKind.InspectionFailed)
        {
            if (physicalIdentity is not null)
            {
                throw new ArgumentException(
                    "A missing or failed destination observation cannot retain physical identity.",
                    nameof(physicalIdentity));
            }
        }
        else if (physicalIdentity is not null)
        {
            PhysicalEntryKind expectedKind = entryKind == DestinationEntryKind.Directory
                ? PhysicalEntryKind.Directory
                : PhysicalEntryKind.RegularFile;
            if (physicalIdentity.EntryKind != expectedKind)
            {
                throw new ArgumentException(
                    "The destination state and physical identity must describe the same entry kind.",
                    nameof(physicalIdentity));
            }
        }

        Path = path;
        EntryKind = entryKind;
        Failure = failure;
        PhysicalIdentity = physicalIdentity;
    }

    /// <summary>Gets the canonical path that was inspected.</summary>
    public CanonicalPath Path { get; }

    /// <summary>Gets what the read-only inspection found.</summary>
    public DestinationEntryKind EntryKind { get; }

    /// <summary>Gets why inspection failed, if it did.</summary>
    public DestinationInspectionFailure? Failure { get; }

    /// <summary>
    /// Gets the strong physical identity captured for an existing ordinary
    /// entry, or <see langword="null"/> when it was unavailable.
    /// </summary>
    public PhysicalEntryIdentity? PhysicalIdentity { get; }
}
