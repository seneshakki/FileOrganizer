using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Core.Tests.Planning;

public sealed class DestinationStateSnapshotCancellationTests
{
    private readonly PathPolicy _paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public void PropagatesCancellationDuringImmutableMaterialization()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => new DestinationStateSnapshot(
                _paths.Semantics,
                CancelBeforeSecondFact(cancellation),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private IEnumerable<DestinationPathFact> CancelBeforeSecondFact(
        CancellationTokenSource cancellation)
    {
        yield return new DestinationPathFact(
            Path(@"C:\Archive\first.txt"),
            DestinationEntryKind.Missing);
        cancellation.Cancel();
        yield return new DestinationPathFact(
            Path(@"C:\Archive\second.txt"),
            DestinationEntryKind.Missing);
    }

    private CanonicalPath Path(string value) =>
        Assert.IsType<CanonicalPath>(_paths.CanonicalizeAbsolute(value).Path);
}
