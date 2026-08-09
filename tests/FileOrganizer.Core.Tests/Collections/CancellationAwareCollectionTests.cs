using FileOrganizer.Core.Collections;

namespace FileOrganizer.Core.Tests.Collections;

public sealed class CancellationAwareCollectionTests
{
    [Fact]
    public void PropagatesCancellationDirectlyDuringMaterialization()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => CancellationAwareCollection.Snapshot(
                CancelBeforeSecondItem(cancellation),
                "items",
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void PropagatesCancellationDirectlyFromTheMergeSortLoop()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        int[] values = [2, 1];
        CancelingComparer comparer = new(cancellation);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => CancellationAwareCollection.Sort(values, comparer, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, comparer.ComparisonCount);
    }

    private static IEnumerable<object> CancelBeforeSecondItem(
        CancellationTokenSource cancellation)
    {
        yield return new object();
        cancellation.Cancel();
        yield return new object();
    }

    private sealed class CancelingComparer(CancellationTokenSource cancellation) : IComparer<int>
    {
        public int ComparisonCount { get; private set; }

        public int Compare(int left, int right)
        {
            ComparisonCount++;
            cancellation.Cancel();
            return left.CompareTo(right);
        }
    }
}
