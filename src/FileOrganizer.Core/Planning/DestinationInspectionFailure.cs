namespace FileOrganizer.Core.Planning;

/// <summary>A structured failure returned by read-only destination inspection.</summary>
public sealed record DestinationInspectionFailure
{
    /// <param name="kind">The stable category of the inspection failure.</param>
    /// <param name="description">A concise explanation suitable for a dry-run preview.</param>
    public DestinationInspectionFailure(
        DestinationInspectionFailureKind kind,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
    }

    /// <summary>Gets the stable category of the failure.</summary>
    public DestinationInspectionFailureKind Kind { get; }

    /// <summary>Gets the explanation suitable for display.</summary>
    public string Description { get; }
}
