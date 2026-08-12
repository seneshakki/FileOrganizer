using System.Buffers.Binary;
using System.Security.Cryptography;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Execution;

internal static class ExecutionManifestFingerprint
{
    internal static PlanFingerprint Compute(
        CanonicalPath sourceRoot,
        PhysicalEntryIdentity sourceRootPhysicalIdentity,
        IReadOnlyList<ExecutionManifestItem> items,
        PathPolicy policy)
    {
        using MemoryStream bytes = new();

        WriteString(bytes, "FileOrganizer.ExecutionManifest");
        WriteInt32(bytes, ExecutionManifest.FingerprintFormatVersion);
        WriteInt32(bytes, (int)sourceRoot.Semantics.Syntax);
        WriteInt32(bytes, (int)sourceRoot.Semantics.CaseSensitivity);
        WriteInt32(bytes, (int)sourceRoot.Semantics.UnicodeNormalization);
        WriteString(bytes, policy.GetDeterministicLogicalKey(sourceRoot));
        WritePhysicalIdentity(bytes, sourceRootPhysicalIdentity);
        WriteInt32(bytes, items.Count);

        foreach (ExecutionManifestItem item in items)
        {
            WriteInt32(bytes, item.Ordinal);
            WriteInt32(bytes, (int)item.ActionKind);
            WriteString(
                bytes,
                policy.GetDeterministicLogicalKey(item.SourcePrecondition.CanonicalPath));
            WritePhysicalIdentity(bytes, item.SourcePrecondition.PhysicalIdentity!);
            WriteInt64(bytes, item.SourcePrecondition.Length);
            WriteOptionalInstant(bytes, item.SourcePrecondition.CreatedUtc);
            WriteInt64(bytes, item.SourcePrecondition.ModifiedUtc.UtcDateTime.Ticks);
            WriteString(bytes, policy.GetDeterministicLogicalKey(item.DestinationDirectory));
            WritePhysicalIdentity(bytes, item.DestinationDirectoryPhysicalIdentity);
            WriteString(bytes, policy.GetDeterministicLogicalKey(item.TargetPath));
        }

        byte[] hash = SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        return new PlanFingerprint(Convert.ToHexStringLower(hash));
    }

    private static void WritePhysicalIdentity(
        Stream destination,
        PhysicalEntryIdentity identity)
    {
        WriteString(destination, identity.Scheme);
        WriteInt32(destination, identity.SchemeVersion);
        WriteString(destination, identity.VolumeIdentity);
        WriteString(destination, identity.EntryIdentifier);
        WriteInt32(destination, (int)identity.EntryKind);
    }

    private static void WriteOptionalInstant(Stream destination, DateTimeOffset? value)
    {
        destination.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            WriteInt64(destination, value.Value.UtcDateTime.Ticks);
        }
    }

    private static void WriteString(Stream destination, string value)
    {
        WriteInt32(destination, value.Length);
        Span<byte> buffer = stackalloc byte[sizeof(char)];
        foreach (char codeUnit in value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, codeUnit);
            destination.Write(buffer);
        }
    }

    private static void WriteInt32(Stream destination, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        destination.Write(buffer);
    }

    private static void WriteInt64(Stream destination, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        destination.Write(buffer);
    }
}
