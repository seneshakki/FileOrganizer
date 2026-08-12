using System.Buffers.Binary;
using System.Text;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>
/// Writes the deliberately specified binary representation used only for
/// journal integrity hashes. Durable JSON formatting is a separate concern.
/// </summary>
internal sealed class CanonicalJournalWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly MemoryStream _stream = new();

    internal void WriteByte(byte value) => _stream.WriteByte(value);

    internal void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    internal void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _stream.Write(bytes);
    }

    internal void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int byteCount = StrictUtf8.GetByteCount(value);
        WriteInt32(byteCount);

        if (byteCount == 0)
        {
            return;
        }

        byte[] bytes = StrictUtf8.GetBytes(value);
        _stream.Write(bytes);
    }

    internal void WriteOptionalString(string? value)
    {
        WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteString(value);
        }
    }

    internal void WriteOptionalInt32(int? value)
    {
        WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            WriteInt32(value.Value);
        }
    }

    internal void WriteOptionalInt64(long? value)
    {
        WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            WriteInt64(value.Value);
        }
    }

    internal void WriteHash(JournalHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        _stream.Write(hash.ToBytes());
    }

    internal void WriteOptionalHash(JournalHash? hash)
    {
        WriteByte(hash is null ? (byte)0 : (byte)1);
        if (hash is not null)
        {
            WriteHash(hash);
        }
    }

    internal void WriteLowerHex(
        string value,
        int expectedHexLength,
        string description) =>
        _stream.Write(JournalIdentifier.Decode(value, expectedHexLength, description));

    internal byte[] ToArray() => _stream.ToArray();

    public void Dispose() => _stream.Dispose();
}
