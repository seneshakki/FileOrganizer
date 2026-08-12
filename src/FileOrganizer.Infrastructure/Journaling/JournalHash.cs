using System.Security.Cryptography;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>A validated lowercase hexadecimal SHA-256 value.</summary>
internal sealed record JournalHash
{
    internal const int ByteLength = 32;
    internal const int HexLength = ByteLength * 2;

    internal JournalHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != HexLength || !value.All(IsLowerHexDigit))
        {
            throw new ArgumentException(
                "A journal hash must contain exactly 64 lowercase hexadecimal characters.",
                nameof(value));
        }

        Value = value;
    }

    internal string Value { get; }

    internal static JournalHash Compute(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    internal byte[] ToBytes() => Convert.FromHexString(Value);

    public override string ToString() => Value;

    private static bool IsLowerHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
