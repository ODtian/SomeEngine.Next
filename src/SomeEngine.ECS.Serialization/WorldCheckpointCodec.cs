using System.Buffers.Binary;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

/// <summary>Byte ranges of the authenticated checkpoint payload.</summary>
public readonly record struct WorldCheckpointInfo(ulong PayloadOffset, ulong PayloadLength, ulong TotalLength);

/// <summary>Authenticated envelope around the version 5 World encoding.</summary>
public static class WorldCheckpointCodec
{
    public const int HeaderSize = 128;

    private const ushort Version = 3;
    private const uint RegistryMatchFlag = 1;
    private const int RegistryHashOffset = 40;
    private const int PayloadHashOffset = 72;
    private const int HeaderHashOffset = 104;

    private static ReadOnlySpan<byte> Magic => "SEWCP003"u8;

    /// <summary>Captures the World, then writes its version 5 payload and authenticated envelope.</summary>
    public static void Write(Stream destination, World world, SerializationRegistry registry)
    {
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Checkpoint destination must be writable and seekable.", nameof(destination));

        using var captured = new WorldCheckpoint();
        captured.Capture(world);
        long checkpointStart = destination.Position;
        Digest256 registryIdentity = ComputeRegistryIdentity(registry);
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        destination.Write(header);
        using var payload = new HashingWriteStream(destination);
        new WorldSerializer(registry).Save(captured, payload);
        Digest256 payloadHash = payload.CompleteDigest();
        ulong payloadLength = checked((ulong)payload.BytesWritten);
        ulong totalLength = checked((ulong)HeaderSize + payloadLength);
        long checkpointEnd = CheckedAbsolute(checkpointStart, totalLength);
        WriteHeader(header, registryIdentity, payloadHash, payloadLength, totalLength);
        destination.Position = checkpointStart;
        destination.Write(header);
        destination.Position = checkpointEnd;
    }

    /// <summary>Reads and authenticates one version 5 World payload.</summary>
    public static World Read(Stream source, SerializationRegistry registry, SerializationReadLimits? limits = null)
    {
        RequireSeekableReadable(source);
        long checkpointStart = source.Position;
        Header header = ReadHeader(source);
        SerializationReadLimits effectiveLimits = limits ?? SerializationReadLimits.Default;
        if (effectiveLimits.MaxCheckpointBytes < 0 ||
            header.PayloadLength > checked((ulong)effectiveLimits.MaxCheckpointBytes))
            throw SerializationErrors.CreateDataException(SerializationError.LimitExceeded,
                "World checkpoint payload exceeds the configured byte limit.");
        if (!ComputeRegistryIdentity(registry).FixedTimeEquals(header.RegistryIdentity))
            throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch,
                "World checkpoint registry identity differs from the supplied registry.");

        source.Position = CheckedAbsolute(checkpointStart, header.PayloadOffset);
        using var payload = new HashingReadStream(source, checked((long)header.PayloadLength));
        World? loaded = null;
        try
        {
            loaded = WorldSerializer.ReadWorld(payload, registry,
                new WorldLoadOptions(IdentityMode: EntityIdentityMode.Preserve,
                    MissingReferenceMode: MissingReferenceMode.Throw, ReadLimits: limits,
                    RequiredPurpose: SavePurpose.RawCheckpoint, RequireEndOfInput: true));
            if (payload.Remaining != 0)
                throw new InvalidDataException("World checkpoint payload length differs from its envelope.");
            if (!payload.CompleteDigest().FixedTimeEquals(header.PayloadHash))
                throw SerializationErrors.CreateDataException(SerializationError.IntegrityMismatch,
                    "World checkpoint payload authentication failed.");
            source.Position = CheckedAbsolute(checkpointStart, header.TotalLength);
            return loaded;
        }
        catch
        {
            loaded?.Dispose();
            throw;
        }
    }

    /// <summary>Authenticates and returns envelope metadata without decoding the World payload.</summary>
    public static WorldCheckpointInfo Inspect(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireSeekableReadable(source);
        long originalPosition = source.Position;
        try
        {
            Header header = ReadHeader(source);
            return new WorldCheckpointInfo(
                header.PayloadOffset,
                header.PayloadLength,
                header.TotalLength);
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static void WriteHeader(
        Span<byte> destination,
        Digest256 registryIdentity,
        Digest256 payloadHash,
        ulong payloadLength,
        ulong totalLength)
    {
        if (destination.Length != HeaderSize)
            throw new ArgumentException("Checkpoint header destination has the wrong size.", nameof(destination));

        destination.Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..12], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], RegistryMatchFlag);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], payloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], totalLength);
        registryIdentity.Write(destination[RegistryHashOffset..PayloadHashOffset]);
        payloadHash.Write(destination[PayloadHashOffset..HeaderHashOffset]);
        ComputeHash(destination[..HeaderHashOffset]).WritePrefix24(destination[HeaderHashOffset..]);
    }

    private static Header ReadHeader(Stream source)
    {
        Span<byte> bytes = stackalloc byte[HeaderSize];
        try
        {
            source.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new EndOfStreamException("Truncated World checkpoint header.", exception);
        }

        if (!bytes[..8].SequenceEqual(Magic))
        {
            throw SerializationErrors.CreateDataException(
                SerializationError.UnsupportedVersion,
                "Unsupported World checkpoint envelope; only current SEWCP003 is accepted.");
        }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        if (version != Version)
            throw SerializationErrors.CreateDataException(
                SerializationError.UnsupportedVersion,
                $"Unsupported World checkpoint version {version}.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]) != HeaderSize)
            throw new InvalidDataException("World checkpoint header size is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]) != RegistryMatchFlag)
            throw new InvalidDataException("World checkpoint flags are invalid.");

        Digest256 expectedHeaderHash = Digest256.ReadPrefix24(bytes[HeaderHashOffset..]);
        Digest256 actualHeaderHash = ComputeHash(bytes[..HeaderHashOffset]);
        if (!actualHeaderHash.FixedTimePrefix24Equals(expectedHeaderHash))
            throw SerializationErrors.CreateDataException(
                SerializationError.IntegrityMismatch,
                "World checkpoint header authentication failed.");

        ulong payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..24]);
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..32]);
        ulong totalLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..40]);
        if (payloadOffset != HeaderSize)
            throw new InvalidDataException("World checkpoint payload offset is not canonical.");
        ulong expectedTotal;
        try
        {
            expectedTotal = checked(payloadOffset + payloadLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("World checkpoint length overflows UInt64.", exception);
        }
        if (totalLength != expectedTotal || payloadLength > long.MaxValue)
            throw new InvalidDataException("World checkpoint total length is invalid.");

        return new Header(
            payloadOffset,
            payloadLength,
            totalLength,
            Digest256.Read(bytes[RegistryHashOffset..PayloadHashOffset]),
            Digest256.Read(bytes[PayloadHashOffset..HeaderHashOffset]));
    }

    private static Digest256 ComputeRegistryIdentity(SerializationRegistry registry)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[32];
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, registry.Types.Count);
        hash.AppendData(count);
        foreach (SavedType type in registry.Types.OrderBy(type => type.Key.StableId))
        {
            SerializationTypeKey key = type.CheckpointKey;
            record.Clear();
            record[0] = (byte)type.Storage;
            record[1] = (byte)type.CodecKind;
            key.StableId.TryWriteBytes(record[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], key.SchemaFingerprint);
            hash.AppendData(record);
            byte[] name = new System.Text.UTF8Encoding(false, true).GetBytes(key.StableName);
            BinaryPrimitives.WriteInt32LittleEndian(count, name.Length);
            hash.AppendData(count);
            hash.AppendData(name);
        }
        return Digest256.Finish(hash);
    }

    private static Digest256 ComputeHash(ReadOnlySpan<byte> bytes) =>
        Digest256.ComputeSha256(bytes);

    private static void RequireSeekableReadable(Stream source)
    {
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "Checkpoint source must be readable and seekable; no resident fallback is permitted.",
                nameof(source));
        }
    }

    private static long CheckedAbsolute(long start, ulong relative)
    {
        if (start < 0 || relative > long.MaxValue)
            throw new InvalidDataException("World checkpoint offset is outside the supported stream range.");
        try
        {
            return checked(start + (long)relative);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("World checkpoint absolute offset overflowed Int64.", exception);
        }
    }

    private readonly record struct Header(
        ulong PayloadOffset,
        ulong PayloadLength,
        ulong TotalLength,
        Digest256 RegistryIdentity,
        Digest256 PayloadHash);
}
