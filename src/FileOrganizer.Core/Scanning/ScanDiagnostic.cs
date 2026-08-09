namespace FileOrganizer.Core.Scanning;

/// <summary>
/// A structured account of an entry the scanner could not safely include.
/// </summary>
public sealed record ScanDiagnostic
{
    /// <param name="kind">The stable category of the scan failure.</param>
    /// <param name="affectedPath">
    /// The canonical path where possible, or the raw path returned by the file
    /// system when canonicalization itself failed.
    /// </param>
    public ScanDiagnostic(ScanDiagnosticKind kind, string affectedPath)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(affectedPath);

        Kind = kind;
        AffectedPath = affectedPath;
    }

    /// <summary>Gets the stable category of the scan failure.</summary>
    public ScanDiagnosticKind Kind { get; }

    /// <summary>Gets the path affected by the failure.</summary>
    public string AffectedPath { get; }
}
