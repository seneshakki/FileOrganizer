using System.Runtime.InteropServices;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

/// <summary>Physical reads are confined to one unique system-temp directory.</summary>
public sealed class WindowsPhysicalIdentityAdapterTests
{
    private readonly PathPolicy _paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.None);

    [Fact]
    public void NativeReadOnlyQueryLayoutsMatchTheDocumentedWindowsStructures()
    {
        Assert.Equal(
            8,
            Marshal.SizeOf<WindowsFileIdentityNative.FileAttributeTagInformation>());
        Assert.Equal(
            24,
            Marshal.SizeOf<WindowsFileIdentityNative.FileStandardInformation>());
        Assert.Equal(
            40,
            Marshal.SizeOf<WindowsFileIdentityNative.FileBasicInformation>());
        Assert.Equal(
            24,
            Marshal.SizeOf<WindowsFileIdentityNative.FileIdentityInformation>());
    }

    [Fact]
    public void ReadsStableStrongIdentityAndMetadataForARegularFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows adapter integration test.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string file = temp.WriteFile("report.txt", "12345");
        WindowsPhysicalIdentityAdapter adapter = new();

        PhysicalEntryObservation first = adapter.Observe(
            Canonical(file),
            TestContext.Current.CancellationToken);
        PhysicalEntryObservation second = adapter.Observe(
            Canonical(file),
            TestContext.Current.CancellationToken);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, first.Outcome);
        Assert.Equal(PhysicalEntryKind.RegularFile, first.EntryKind);
        Assert.Equal(first.Identity, second.Identity);
        PhysicalEntryIdentity identity = Assert.IsType<PhysicalEntryIdentity>(first.Identity);
        Assert.Equal(PhysicalEntryIdentity.WindowsFileIdScheme, identity.Scheme);
        Assert.Equal(PhysicalEntryIdentity.WindowsFileIdSchemeVersion, identity.SchemeVersion);
        Assert.Equal(16, identity.VolumeIdentity.Length);
        Assert.Equal(32, identity.EntryIdentifier.Length);

        PhysicalFileMetadata metadata =
            Assert.IsType<PhysicalFileMetadata>(first.FileMetadata);
        Assert.Equal(5, metadata.Length);
        Assert.Equal(TimeSpan.Zero, metadata.ModifiedUtc.Offset);
        Assert.NotNull(metadata.CreatedUtc);
        Assert.Equal(TimeSpan.Zero, metadata.CreatedUtc.Value.Offset);
    }

    [Fact]
    public void ReadsDirectoryIdentityForScanRootAndDestinationDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows adapter integration test.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string destination = Path.Combine(temp.FullPath, "destination");
        Directory.CreateDirectory(destination);
        WindowsPhysicalIdentityAdapter adapter = new();

        PhysicalEntryObservation scanRoot = adapter.Observe(
            Canonical(temp.FullPath),
            TestContext.Current.CancellationToken);
        PhysicalEntryObservation destinationFirst = adapter.Observe(
            Canonical(destination),
            TestContext.Current.CancellationToken);
        PhysicalEntryObservation destinationSecond = adapter.Observe(
            Canonical(destination),
            TestContext.Current.CancellationToken);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, scanRoot.Outcome);
        Assert.Equal(PhysicalEntryKind.Directory, scanRoot.EntryKind);
        Assert.Null(scanRoot.FileMetadata);
        Assert.Equal(PhysicalEntryObservationOutcome.Success, destinationFirst.Outcome);
        Assert.Equal(PhysicalEntryKind.Directory, destinationFirst.EntryKind);
        Assert.Equal(destinationFirst.Identity, destinationSecond.Identity);
        Assert.NotEqual(scanRoot.Identity, destinationFirst.Identity);
    }

    [Fact]
    public void MissingEntryReturnsStructuredMissingOutcome()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows adapter integration test.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        WindowsPhysicalIdentityAdapter adapter = new();

        PhysicalEntryObservation observation = adapter.Observe(
            Canonical(Path.Combine(temp.FullPath, "missing.txt")),
            TestContext.Current.CancellationToken);

        Assert.Equal(PhysicalEntryObservationOutcome.Missing, observation.Outcome);
        Assert.Equal(PhysicalEntryObservationReason.EntryMissing, observation.Reason);
        Assert.Null(observation.Identity);
        Assert.NotNull(observation.NativeErrorCode);
    }

    [Fact]
    public void ReparsePointIsObservedWithoutFollowingItsTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows adapter integration test.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string target = temp.WriteFile("target.txt", "target");
        string link = Path.Combine(temp.FullPath, "link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip(
                $"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
        }

        WindowsPhysicalIdentityAdapter adapter = new();

        PhysicalEntryObservation targetObservation = adapter.Observe(
            Canonical(target),
            TestContext.Current.CancellationToken);
        PhysicalEntryObservation linkObservation = adapter.Observe(
            Canonical(link),
            TestContext.Current.CancellationToken);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, targetObservation.Outcome);
        Assert.Equal(PhysicalEntryKind.RegularFile, targetObservation.EntryKind);
        Assert.Equal(PhysicalEntryObservationOutcome.Unsupported, linkObservation.Outcome);
        Assert.Equal(PhysicalEntryObservationReason.ReparsePoint, linkObservation.Reason);
        Assert.Equal(PhysicalEntryKind.ReparsePoint, linkObservation.EntryKind);
        Assert.Null(linkObservation.Identity);
        Assert.Null(linkObservation.FileMetadata);
    }

    [Theory]
    [InlineData(5, PhysicalEntryObservationOutcome.Inaccessible, PhysicalEntryObservationReason.AccessDenied)]
    [InlineData(32, PhysicalEntryObservationOutcome.Inaccessible, PhysicalEntryObservationReason.SharingViolation)]
    [InlineData(50, PhysicalEntryObservationOutcome.Unsupported, PhysicalEntryObservationReason.UnsupportedCapability)]
    public void NativeOpenFailuresRemainStructured(
        int errorCode,
        PhysicalEntryObservationOutcome expectedOutcome,
        PhysicalEntryObservationReason expectedReason)
    {
        PhysicalEntryObservation observation =
            WindowsPhysicalIdentityAdapter.FromOpenFailure(errorCode);

        Assert.Equal(expectedOutcome, observation.Outcome);
        Assert.Equal(expectedReason, observation.Reason);
        Assert.Equal(errorCode, observation.NativeErrorCode);
        Assert.Null(observation.Identity);
    }

    [Fact]
    public void NonWindowsCapabilityIsExplicitAndDoesNotInvokeNativeCode()
    {
        WindowsPhysicalIdentityAdapter adapter = new(isWindows: false);

        PhysicalEntryObservation observation = adapter.Observe(
            Canonical(@"C:\Source\report.txt"),
            TestContext.Current.CancellationToken);

        Assert.Equal(PhysicalEntryObservationOutcome.Unsupported, observation.Outcome);
        Assert.Equal(PhysicalEntryObservationReason.UnsupportedCapability, observation.Reason);
        Assert.Null(observation.Identity);
        Assert.Null(observation.NativeErrorCode);
    }

    [Fact]
    public void PreCanceledObservationPropagatesCancellation()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        WindowsPhysicalIdentityAdapter adapter = new(isWindows: false);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => adapter.Observe(Canonical(@"C:\Source\report.txt"), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private CanonicalPath Canonical(string path)
    {
        PathResolution resolution = _paths.CanonicalizeAbsolute(path);
        return Assert.IsType<CanonicalPath>(resolution.Path);
    }
}
