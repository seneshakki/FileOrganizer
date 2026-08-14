using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.Watching;

namespace FileOrganizer.Infrastructure.Tests.Watching;

public sealed class WindowsFileStabilityObservationAdapterTests
{
    [Fact]
    public async Task ObservationDelegatesToTheExistingWindowsIdentityCapability()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            "FileOrganizer.Tests",
            Guid.NewGuid().ToString("N"),
            "missing.txt");
        PathPolicy policy = new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.None);
        CanonicalPath path = policy.CanonicalizeAbsolute(missingPath).Path!;
        WindowsFileStabilityObservationAdapter adapter = new();

        PhysicalEntryObservation result = await adapter.ObserveAsync(
            path,
            TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PhysicalEntryObservationOutcome.Missing, result.Outcome);
            Assert.Equal(PhysicalEntryObservationReason.EntryMissing, result.Reason);
        }
        else
        {
            Assert.Equal(PhysicalEntryObservationOutcome.Unsupported, result.Outcome);
            Assert.Equal(PhysicalEntryObservationReason.UnsupportedCapability, result.Reason);
        }
    }

    [Fact]
    public async Task PreCanceledObservationDoesNotTouchTheFilesystem()
    {
        PathPolicy policy = new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.None);
        CanonicalPath path = policy.CanonicalizeAbsolute(@"C:\NeverObserved.txt").Path!;
        WindowsFileStabilityObservationAdapter adapter = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => adapter.ObserveAsync(path, cancellation.Token).AsTask());
    }
}
