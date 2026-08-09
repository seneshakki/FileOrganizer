using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Tests.Physical;

public sealed class PhysicalEntryObservationTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 8, 9, 11, 12, 13, TimeSpan.FromHours(3));

    private static readonly DateTimeOffset Modified =
        new(2026, 8, 10, 11, 12, 13, TimeSpan.FromHours(-4));

    [Fact]
    public void SuccessfulFileObservationRequiresIdentityAndSameHandleMetadata()
    {
        PhysicalEntryIdentity identity = Identity(PhysicalEntryKind.RegularFile);
        PhysicalFileMetadata metadata = new(42, Created, Modified);

        PhysicalEntryObservation observation =
            PhysicalEntryObservation.Success(identity, metadata);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, observation.Outcome);
        Assert.Equal(PhysicalEntryObservationReason.None, observation.Reason);
        Assert.Equal(PhysicalEntryKind.RegularFile, observation.EntryKind);
        Assert.Same(identity, observation.Identity);
        Assert.Equal(42, Assert.IsType<PhysicalFileMetadata>(observation.FileMetadata).Length);
        Assert.Equal(TimeSpan.Zero, observation.FileMetadata.CreatedUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, observation.FileMetadata.ModifiedUtc.Offset);
        Assert.Null(observation.NativeErrorCode);
    }

    [Fact]
    public void SuccessfulDirectoryObservationHasIdentityWithoutFileMetadata()
    {
        PhysicalEntryIdentity identity = Identity(PhysicalEntryKind.Directory);

        PhysicalEntryObservation observation = PhysicalEntryObservation.Success(identity);

        Assert.Equal(PhysicalEntryKind.Directory, observation.EntryKind);
        Assert.Null(observation.FileMetadata);
    }

    [Fact]
    public void IdentityFailureMayRetainImmutableFileMetadataForPreview()
    {
        PhysicalFileMetadata metadata = new(42, Created, Modified);

        PhysicalEntryObservation observation = PhysicalEntryObservation.Unsupported(
            PhysicalEntryObservationReason.UnsupportedCapability,
            PhysicalEntryKind.RegularFile,
            metadata,
            nativeErrorCode: 50);

        Assert.Equal(PhysicalEntryObservationOutcome.Unsupported, observation.Outcome);
        Assert.Equal(PhysicalEntryObservationReason.UnsupportedCapability, observation.Reason);
        Assert.Null(observation.Identity);
        Assert.Same(metadata, observation.FileMetadata);
        Assert.Equal(50, observation.NativeErrorCode);
    }

    [Fact]
    public void ReparsePointReasonRequiresReparsePointKind()
    {
        Assert.Throws<ArgumentException>(
            () => PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.ReparsePoint,
                PhysicalEntryKind.Directory));
    }

    [Fact]
    public void FailureFactoriesRejectReasonsFromAnotherOutcome()
    {
        Assert.Throws<ArgumentException>(
            () => PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.UnsupportedCapability));
    }

    private static PhysicalEntryIdentity Identity(PhysicalEntryKind kind) =>
        new(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            "0000000000000001",
            "00112233445566778899AABBCCDDEEFF",
            kind);
}
