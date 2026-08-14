namespace FileOrganizer.Application.Watching;

/// <summary>An immutable structured watch diagnostic without machine-specific path text.</summary>
public sealed record WatchDiagnostic
{
    public WatchDiagnostic(
        WatchDiagnosticKind kind,
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

    public WatchDiagnosticKind Kind { get; }

    public string Description { get; }

    public int? NativeErrorCode { get; }
}
