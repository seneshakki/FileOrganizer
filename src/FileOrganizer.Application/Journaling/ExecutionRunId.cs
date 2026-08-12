using System.Security.Cryptography;

namespace FileOrganizer.Application.Journaling;

/// <summary>Identifies one execution attempt independently of its plan fingerprint.</summary>
public sealed record ExecutionRunId
{
    /// <summary>The fixed number of lowercase hexadecimal characters in a run identifier.</summary>
    public const int HexLength = 32;

    public ExecutionRunId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != HexLength || !value.All(IsLowerAsciiHexDigit))
        {
            throw new ArgumentException(
                "An execution run identifier must contain exactly 32 lowercase hexadecimal characters.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the exact lowercase identifier.</summary>
    public string Value { get; }

    /// <summary>Creates a new cryptographically random 128-bit execution identifier.</summary>
    public static ExecutionRunId CreateNew()
    {
        Span<byte> bytes = stackalloc byte[HexLength / 2];
        RandomNumberGenerator.Fill(bytes);
        return new ExecutionRunId(Convert.ToHexStringLower(bytes));
    }

    public override string ToString() => Value;

    private static bool IsLowerAsciiHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f';
}
