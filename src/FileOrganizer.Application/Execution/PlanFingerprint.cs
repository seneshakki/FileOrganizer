namespace FileOrganizer.Application.Execution;

/// <summary>
/// The canonical SHA-256 identity of an immutable execution manifest.
/// </summary>
public sealed record PlanFingerprint
{
    /// <summary>The number of lowercase hexadecimal characters in a fingerprint.</summary>
    public const int HexLength = 64;

    public PlanFingerprint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != HexLength || !value.All(IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "A plan fingerprint must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    /// <summary>Gets the normalized lowercase SHA-256 value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsAsciiHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
