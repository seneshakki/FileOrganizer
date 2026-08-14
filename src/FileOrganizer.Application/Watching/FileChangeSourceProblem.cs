namespace FileOrganizer.Application.Watching;

/// <summary>An immutable, machine-data-free watcher-source diagnostic.</summary>
public sealed record FileChangeSourceProblem
{
    public FileChangeSourceProblem(
        FileChangeSourceProblemKind kind,
        string description,
        int? nativeErrorCode = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (nativeErrorCode.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nativeErrorCode.Value);
        }

        Kind = kind;
        Description = description;
        NativeErrorCode = nativeErrorCode;
    }

    public FileChangeSourceProblemKind Kind { get; }

    public string Description { get; }

    public int? NativeErrorCode { get; }
}
