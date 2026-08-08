using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SomeEngine.Serialization.Containers;

public enum ChunkCompression : byte
{
    None = 0,
    Brotli = 1,
}

public readonly record struct BinaryChunkEntry(
    ulong Key,
    ulong TypeFingerprint,
    long Offset,
    long StoredLength,
    long DecodedLength,
    int Alignment,
    ChunkCompression Compression,
    Digest256 ContentHash,
    uint Ordinal)
{
    public long EndOffset => checked(Offset + StoredLength);
}

public readonly record struct BinaryWireTypeDescriptor(
    Guid TypeId,
    ulong SchemaFingerprint,
    BinaryCompatibility Compatibility,
    uint SchemaEpoch);

internal readonly record struct BinaryDocumentHeader(
    BinaryCompatibility Compatibility,
    uint SchemaEpoch,
    uint ChunkCount,
    ulong SchemaFingerprint,
    Guid Generation,
    uint CatalogLength,
    long RootOffset,
    long RootLength,
    long DirectoryOffset,
    long DirectoryLength,
    long TotalLength,
    Digest256 RootHash);

internal static class BinaryDocumentFormat
{
    internal const int HeaderSize = 128;
    internal const int DirectoryEntrySize = 96;
    internal const ushort Version = 3;
    internal const int HashSize = Digest256.Size;
    private static ReadOnlySpan<byte> Magic => "SEBDOC03"u8;

    internal static void WriteHeader(Span<byte> destination, in BinaryDocumentHeader header)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException("Binary document header destination is too small.", nameof(destination));
        destination[..HeaderSize].Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], HeaderSize);
        destination[12] = (byte)header.Compatibility;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], DirectoryEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], header.SchemaEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], header.ChunkCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.SchemaFingerprint);
        if (!header.Generation.TryWriteBytes(destination[32..48], bigEndian: true, out int written) || written != 16)
            throw new InvalidOperationException("Unable to encode binary document generation.");
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..], header.RootOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], header.RootLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[64..], header.DirectoryOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[72..], header.DirectoryLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[80..], header.TotalLength);
        header.RootHash.Write(destination[88..120]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[120..], header.CatalogLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[124..], Checksum32(destination[..124]));
    }

    internal static BinaryDocumentHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
            throw new InvalidDataException("Truncated binary document header.");
        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(source[124..]);
        if (Checksum32(source[..124]) != expectedChecksum)
            throw new InvalidDataException("Binary document header checksum is invalid.");
        if (!source[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Binary document magic is invalid.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]);
        if (version != Version)
            throw new InvalidDataException($"Unsupported binary document version {version}.");
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source[10..]);
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"Binary document header size {headerSize} is invalid.");
        if (!Enum.IsDefined((BinaryCompatibility)source[12]))
            throw new InvalidDataException($"Unknown binary compatibility mode {source[12]}.");
        if (source[13] != 0)
            throw new InvalidDataException("Binary document header reserved byte is non-zero.");
        ushort entrySize = BinaryPrimitives.ReadUInt16LittleEndian(source[14..]);
        if (entrySize != DirectoryEntrySize)
            throw new InvalidDataException($"Binary directory entry size {entrySize} is invalid.");

        return new BinaryDocumentHeader(
            (BinaryCompatibility)source[12],
            BinaryPrimitives.ReadUInt32LittleEndian(source[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
            new Guid(source[32..48], bigEndian: true),
            BinaryPrimitives.ReadUInt32LittleEndian(source[120..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[64..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[72..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[80..]),
            Digest256.Read(source[88..120]));
    }

    internal static byte[] WriteTypeCatalog(IEnumerable<BinaryWireTypeDescriptor> descriptors)
    {
        BinaryWireTypeDescriptor[] ordered = descriptors
            .OrderBy(static descriptor => descriptor.TypeId)
            .ToArray();
        int payloadLength = checked(4 + ordered.Length * 32);
        byte[] bytes = new byte[checked(payloadLength + HashSize)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            BinaryWireTypeDescriptor descriptor = ordered[i];
            if (descriptor.TypeId == Guid.Empty || descriptor.SchemaFingerprint == 0 || descriptor.SchemaEpoch == 0)
                throw new InvalidOperationException("Binary type catalog contains an invalid descriptor.");
            Span<byte> entry = bytes.AsSpan(4 + i * 32, 32);
            if (!descriptor.TypeId.TryWriteBytes(entry[..16], bigEndian: true, out int written) || written != 16)
                throw new InvalidOperationException("Unable to encode binary type id.");
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], descriptor.SchemaFingerprint);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], descriptor.SchemaEpoch);
            entry[28] = (byte)descriptor.Compatibility;
        }
        SHA256.HashData(bytes.AsSpan(0, payloadLength), bytes.AsSpan(payloadLength, HashSize));
        return bytes;
    }

    internal static BinaryWireTypeDescriptor[] ReadTypeCatalog(
        ReadOnlySpan<byte> bytes,
        BinaryReadLimits limits)
    {
        if (bytes.Length < 4 + HashSize)
            throw new InvalidDataException("Binary type catalog is truncated.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || count > limits.MaxTypeCatalogEntries)
            throw new InvalidDataException($"Binary type catalog count {count} exceeds configured limits.");
        int expected;
        try
        {
            expected = checked(4 + count * 32 + HashSize);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Binary type catalog length overflowed.", exception);
        }
        if (bytes.Length != expected)
            throw new InvalidDataException("Binary type catalog length does not match its entry count.");
        long allocationEstimate = checked(256L + (long)count * 112L);
        if (allocationEstimate > limits.MaxAllocationBytes)
        {
            throw new InvalidDataException(
                $"Binary type catalog allocation estimate {allocationEstimate} exceeds configured " +
                $"allocation limit {limits.MaxAllocationBytes}.");
        }
        int payloadLength = expected - HashSize;
        Span<byte> actualHash = stackalloc byte[HashSize];
        SHA256.HashData(bytes[..payloadLength], actualHash);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, bytes[payloadLength..]))
            throw new InvalidDataException("Binary type catalog checksum is invalid.");

        var result = new BinaryWireTypeDescriptor[count];
        var typeIds = new HashSet<Guid>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = bytes.Slice(4 + i * 32, 32);
            Guid typeId = new(entry[..16], bigEndian: true);
            ulong fingerprint = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
            BinaryCompatibility compatibility = (BinaryCompatibility)entry[28];
            if (!entry[29..32].SequenceEqual("\0\0\0"u8))
                throw new InvalidDataException("Binary type catalog reserved bytes are non-zero.");
            if (typeId == Guid.Empty || fingerprint == 0 || epoch == 0 || !Enum.IsDefined(compatibility))
                throw new InvalidDataException("Binary type catalog contains an invalid descriptor.");
            if (!typeIds.Add(typeId))
                throw new InvalidDataException($"Binary type catalog contains duplicate type id {typeId}.");
            result[i] = new BinaryWireTypeDescriptor(typeId, fingerprint, compatibility, epoch);
        }
        return result;
    }

    internal static void WriteEntry(Span<byte> destination, in BinaryChunkEntry entry)
    {
        if (destination.Length < DirectoryEntrySize)
            throw new ArgumentException("Binary directory entry destination is too small.", nameof(destination));
        destination[..DirectoryEntrySize].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(destination, entry.Key);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], entry.TypeFingerprint);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], entry.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], entry.StoredLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], entry.DecodedLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], entry.Alignment);
        destination[44] = (byte)entry.Compression;
        entry.ContentHash.Write(destination[48..80]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[80..], entry.Ordinal);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[84..], Checksum32(destination[..84]));
    }

    internal static BinaryChunkEntry ReadEntry(ReadOnlySpan<byte> source)
    {
        if (source.Length < DirectoryEntrySize)
            throw new InvalidDataException("Truncated binary directory entry.");
        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(source[84..]);
        if (Checksum32(source[..84]) != expectedChecksum)
            throw new InvalidDataException("Binary directory entry checksum is invalid.");
        if (!source[45..48].SequenceEqual("\0\0\0"u8)
            || ContainsNonZero(source[88..96]))
        {
            throw new InvalidDataException("Binary directory entry reserved bytes are non-zero.");
        }
        ChunkCompression compression = (ChunkCompression)source[44];
        if (!Enum.IsDefined(compression))
            throw new InvalidDataException($"Unknown chunk compression codec {source[44]}.");

        return new BinaryChunkEntry(
            BinaryPrimitives.ReadUInt64LittleEndian(source),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[40..]),
            compression,
            Digest256.Read(source[48..80]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[80..]));
    }

    internal static Digest256 Hash(ReadOnlySpan<byte> bytes) => Digest256.ComputeSha256(bytes);

    internal static Digest256 ComputeAuthenticationDigest(
        in Digest256 headerHash,
        in Digest256 catalogHash,
        in Digest256 rootHash,
        in Digest256 directoryHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SEBDOCAUTH1"u8);
        Span<byte> encoded = stackalloc byte[Digest256.Size];
        AppendDigest(hash, headerHash, encoded);
        AppendDigest(hash, catalogHash, encoded);
        AppendDigest(hash, rootHash, encoded);
        AppendDigest(hash, directoryHash, encoded);
        return Digest256.Finish(hash);
    }

    private static void AppendDigest(
        IncrementalHash hash,
        in Digest256 digest,
        Span<byte> encoded)
    {
        digest.Write(encoded);
        hash.AppendData(encoded);
    }

    private static uint Checksum32(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[HashSize];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
                return true;
        }
        return false;
    }

    internal static long Align(long value, int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two.");
        long mask = alignment - 1L;
        return checked((value + mask) & ~mask);
    }
}
