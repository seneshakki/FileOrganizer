using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Execution;

internal static class TestPhysicalIdentities
{
    internal static PhysicalEntryIdentity File(
        string entryIdentifier,
        string volumeIdentity = "test-volume",
        string scheme = "test-file-id",
        int schemeVersion = 1) =>
        new(
            scheme,
            schemeVersion,
            volumeIdentity,
            entryIdentifier,
            PhysicalEntryKind.RegularFile);

    internal static PhysicalEntryIdentity Directory(
        string entryIdentifier,
        string volumeIdentity = "test-volume",
        string scheme = "test-file-id",
        int schemeVersion = 1) =>
        new(
            scheme,
            schemeVersion,
            volumeIdentity,
            entryIdentifier,
            PhysicalEntryKind.Directory);
}
