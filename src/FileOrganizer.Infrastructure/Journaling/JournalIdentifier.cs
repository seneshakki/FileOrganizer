namespace FileOrganizer.Infrastructure.Journaling;

internal static class JournalIdentifier
{
    internal const int FingerprintHexLength = 64;
    internal const int RunIdHexLength = 32;

    internal static string RequireFingerprint(string value) =>
        RequireLowerHex(value, FingerprintHexLength, "plan fingerprint");

    internal static string RequireRunId(string value) =>
        RequireLowerHex(value, RunIdHexLength, "execution run identifier");

    internal static byte[] Decode(string value, int expectedHexLength, string description)
    {
        RequireLowerHex(value, expectedHexLength, description);
        return Convert.FromHexString(value);
    }

    private static string RequireLowerHex(
        string value,
        int expectedLength,
        string description)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != expectedLength || !value.All(IsLowerHexDigit))
        {
            throw new ArgumentException(
                $"A {description} must contain exactly {expectedLength} lowercase hexadecimal characters.",
                nameof(value));
        }

        return value;
    }

    private static bool IsLowerHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
