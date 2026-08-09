using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Tests.Physical;

public sealed class PhysicalEntryIdentityTests
{
    [Fact]
    public void EqualValuesRepresentTheSamePhysicalIdentity()
    {
        PhysicalEntryIdentity first = Identity("0000000000000001", "00112233445566778899AABBCCDDEEFF");
        PhysicalEntryIdentity second = Identity("0000000000000001", "00112233445566778899AABBCCDDEEFF");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void VolumeIdentityParticipatesInEquality()
    {
        PhysicalEntryIdentity first = Identity("0000000000000001", "00112233445566778899AABBCCDDEEFF");
        PhysicalEntryIdentity second = Identity("0000000000000002", "00112233445566778899AABBCCDDEEFF");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ObjectKindParticipatesInEquality()
    {
        PhysicalEntryIdentity file = Identity(
            "0000000000000001",
            "00112233445566778899AABBCCDDEEFF",
            PhysicalEntryKind.RegularFile);
        PhysicalEntryIdentity directory = Identity(
            "0000000000000001",
            "00112233445566778899AABBCCDDEEFF",
            PhysicalEntryKind.Directory);

        Assert.NotEqual(file, directory);
    }

    [Fact]
    public void CallerOwnedCharacterStorageCannotMutateIdentityValues()
    {
        char[] source = "00112233445566778899AABBCCDDEEFF".ToCharArray();
        string identifier = new(source);
        PhysicalEntryIdentity identity = Identity("0000000000000001", identifier);

        source[0] = 'F';

        Assert.Equal("00112233445566778899AABBCCDDEEFF", identity.EntryIdentifier);
    }

    [Theory]
    [InlineData(PhysicalEntryKind.ReparsePoint)]
    [InlineData(PhysicalEntryKind.Unsupported)]
    public void UnsafeKindsCannotReceiveExecutableIdentity(PhysicalEntryKind kind)
    {
        Assert.Throws<ArgumentException>(
            () => Identity(
                "0000000000000001",
                "00112233445566778899AABBCCDDEEFF",
                kind));
    }

    private static PhysicalEntryIdentity Identity(
        string volume,
        string identifier,
        PhysicalEntryKind kind = PhysicalEntryKind.RegularFile) =>
        new(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            volume,
            identifier,
            kind);
}
