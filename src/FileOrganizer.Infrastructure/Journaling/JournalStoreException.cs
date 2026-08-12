namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>Reports a durable journal that is missing, corrupt, unsafe, or unreadable.</summary>
public sealed class JournalStoreException : IOException
{
    public JournalStoreException(JournalLoadResult loadResult)
        : base(
            loadResult is { Problems.Count: > 0 } && loadResult.Problems[0] is { } problem
                ? $"The durable execution journal could not be loaded safely: {problem.Kind}: {problem.Description}"
                : "The durable execution journal could not be loaded safely.")
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        LoadResult = loadResult;
    }

    public JournalLoadResult LoadResult { get; }
}
