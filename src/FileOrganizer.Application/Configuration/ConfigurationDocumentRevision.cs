using System.Security.Cryptography;

namespace FileOrganizer.Application.Configuration;

/// <summary>
/// An immutable content revision captured from the exact bytes of one configuration document.
/// </summary>
public sealed record ConfigurationDocumentRevision
{
    private ConfigurationDocumentRevision(long byteLength, string sha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        ByteLength = byteLength;
        Sha256 = sha256;
    }

    public long ByteLength { get; }

    public string Sha256 { get; }

    public static ConfigurationDocumentRevision FromBytes(ReadOnlySpan<byte> bytes) =>
        new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
}
