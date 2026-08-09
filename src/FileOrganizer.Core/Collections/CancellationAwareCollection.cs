namespace FileOrganizer.Core.Collections;

/// <summary>
/// Materializes and sorts immutable-model inputs while keeping cancellation
/// observable during potentially large CPU-bound collection work.
/// </summary>
internal static class CancellationAwareCollection
{
    public static T[] Snapshot<T>(
        IEnumerable<T> source,
        string parameterName,
        CancellationToken cancellationToken)
        where T : class
    {
        if (source is null)
        {
            throw new ArgumentNullException(parameterName);
        }
        cancellationToken.ThrowIfCancellationRequested();

        List<T> materialized = [];
        foreach (T item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item, parameterName);
            materialized.Add(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        T[] snapshot = new T[materialized.Count];
        for (int index = 0; index < materialized.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot[index] = materialized[index];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    /// <summary>
    /// Performs a stable merge sort. Token checks are outside comparer calls,
    /// so cancellation is never wrapped by Array.Sort's comparer exception.
    /// </summary>
    public static void Sort<T>(
        T[] items,
        IComparer<T> comparer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(comparer);
        cancellationToken.ThrowIfCancellationRequested();

        if (items.Length < 2)
        {
            return;
        }

        T[] buffer = new T[items.Length];
        MergeSort(items, buffer, 0, items.Length, comparer, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void MergeSort<T>(
        T[] items,
        T[] buffer,
        int start,
        int end,
        IComparer<T> comparer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (end - start < 2)
        {
            return;
        }

        int middle = start + ((end - start) / 2);
        MergeSort(items, buffer, start, middle, comparer, cancellationToken);
        MergeSort(items, buffer, middle, end, comparer, cancellationToken);

        int left = start;
        int right = middle;
        int destination = start;

        while (left < middle && right < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int comparison = comparer.Compare(items[left], items[right]);
            cancellationToken.ThrowIfCancellationRequested();

            buffer[destination++] = comparison <= 0
                ? items[left++]
                : items[right++];
        }

        while (left < middle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer[destination++] = items[left++];
        }

        while (right < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer[destination++] = items[right++];
        }

        for (int index = start; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items[index] = buffer[index];
        }
    }
}
