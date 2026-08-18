using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Tests.Watching;

public sealed class WatchSnapshotContractTests
{
    private static readonly Guid RootId =
        Guid.Parse("22222222-bbbb-4444-8888-222222222222");

    private static readonly PathPolicy Paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public void SnapshotTokenRequiresRootIdentityAndNonNegativeGeneration()
    {
        Assert.Throws<ArgumentException>(() => new WatchSnapshotToken(Guid.Empty, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatchSnapshotToken(RootId, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatchSnapshotToken(RootId, 0, -1));

        WatchSnapshotToken token = new(RootId, 17, 23);

        Assert.Equal(RootId, token.WatchRootId);
        Assert.Equal(17, token.EventGeneration);
        Assert.Equal(23, token.SynchronizationEpoch);
        Assert.Equal(token, new WatchSnapshotToken(RootId, 17, 23));
    }

    [Fact]
    public void RefreshRequestCarriesOnlyOneValidatedAffectedPath()
    {
        CanonicalPath affectedPath = Canonical(@"C:\Watch\removed.txt");

        WatchRootNotification notification =
            WatchRootNotification.RefreshRequested(RootId, affectedPath);

        Assert.Equal(RootId, notification.WatchRootId);
        Assert.Equal(WatchRootNotificationKind.RefreshRequested, notification.Kind);
        Assert.Same(affectedPath, notification.AffectedPath);
        Assert.Null(notification.State);
        Assert.Null(notification.Candidate);
        Assert.Null(notification.Diagnostic);
        Assert.Throws<ArgumentException>(
            () => WatchRootNotification.RefreshRequested(RootId, Canonical(@"C:\")));
    }

    private static CanonicalPath Canonical(string path)
    {
        PathResolution resolution = Paths.CanonicalizeAbsolute(path);
        Assert.True(resolution.Succeeded, resolution.Problem?.Description);
        return resolution.Path!;
    }
}
