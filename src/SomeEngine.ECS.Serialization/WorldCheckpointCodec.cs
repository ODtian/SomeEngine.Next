using System.Buffers.Binary;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

/// <summary>Metadata for the current canonical World checkpoint envelope.</summary>
public readonly record struct WorldCheckpointInfo(
    ulong PayloadOffset,
    ulong PayloadLength,
    ulong TotalLength);

/// <summary>
/// Exact-registry checkpoint envelope over the one canonical <see cref="WorldSerializer"/> World
/// wire. The checkpoint layer hashes bytes online and never owns a World snapshot, encoded payload
/// buffer, section DTO, native-layout dump, or second component/topology codec.
/// </summary>
public static class WorldCheckpointCodec
{
    public const int HeaderSize = 128;

    private const ushort Version = 3;
    private const uint ExactRegistryFlag = 1;
    private const int RegistryHashOffset = 40;
    private const int PayloadHashOffset = 72;
    private const int HeaderHashOffset = 104;

    private static ReadOnlySpan<byte> Magic => "SEWCP003"u8;

    /// <summary>
    /// Writes one current RawCheckpoint World payload directly to the final seekable sink. Header
    /// reservation, canonical encoding, online hashing, and header backpatch use the canonical
    /// serializer's retained read root; topology admission ends before caller-controlled I/O.
    /// </summary>
    public static void Write(Stream destination, World world, SerializationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException(
                "Checkpoint destination must be writable and seekable; checkpoint encoding " +
                "never stages a non-seekable output in memory.",
                nameof(destination));
        }

        long checkpointStart = destination.Position;
        Digest256 registryIdentity = ComputeRegistryIdentity(registry);
        using var payload = new HashingWriteStream(destination);

        WorldSerializer.WriteWorldCore(
            payload,
            world,
            registry,
            new SerializeOptions(Contract: SerializationContract.RawCheckpoint),
            beforeOutput: () =>
            {
                Span<byte> placeholder = stackalloc byte[HeaderSize];
                placeholder.Clear();
                destination.Write(placeholder);
            },
            afterOutput: () =>
            {
                Digest256 payloadHash = payload.CompleteDigest();
                ulong payloadLength = checked((ulong)payload.BytesWritten);
                ulong totalLength = checked((ulong)HeaderSize + payloadLength);
                long checkpointEnd = CheckedAbsolute(checkpointStart, totalLength);

                Span<byte> header = stackalloc byte[HeaderSize];
                WriteHeader(header, registryIdentity, payloadHash, payloadLength, totalLength);
                destination.Position = checkpointStart;
                destination.Write(header);
                destination.Position = checkpointEnd;
            });
    }

    /// <summary>
    /// Reads the single canonical payload directly into a newly constructed World. A payload or
    /// header authentication failure disposes that World; no existing World apply API exists.
    /// </summary>
    public static World Read(
        Stream source,
        SerializationRegistry registry,
        SerializationReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        RequireSeekableReadable(source);

        long checkpointStart = source.Position;
        Header header = ReadHeader(source);
        SerializationReadLimits effectiveLimits = limits ?? SerializationReadLimits.Default;
        if (effectiveLimits.MaxCheckpointBytes < 0 ||
            header.PayloadLength > checked((ulong)effectiveLimits.MaxCheckpointBytes))
        {
            throw new InvalidDataException(
                $"World checkpoint payload length {header.PayloadLength} exceeds the configured " +
                $"limit {effectiveLimits.MaxCheckpointBytes}.");
        }
        Digest256 localRegistry = ComputeRegistryIdentity(registry);
        if (!localRegistry.FixedTimeEquals(header.RegistryIdentity))
        {
            throw new InvalidDataException(
                "World checkpoint registry identity does not match the current exact registry.");
        }

        source.Position = CheckedAbsolute(checkpointStart, header.PayloadOffset);
        using var payload = new HashingReadStream(source, checked((long)header.PayloadLength));
        World? loaded = null;
        try
        {
            loaded = WorldSerializer.ReadWorld(
                payload,
                registry,
                new WorldLoadOptions(
                    IdentityMode: EntityIdentityMode.Preserve,
                    MissingReferenceMode: MissingReferenceMode.Throw,
                    ReadLimits: limits,
                    RequiredContract: SerializationContract.RawCheckpoint));
            if (payload.Remaining != 0)
            {
                throw new InvalidDataException(
                    "World checkpoint payload contains trailing or truncated bytes.");
            }
            Digest256 actualPayloadHash = payload.CompleteDigest();
            if (!actualPayloadHash.FixedTimeEquals(header.PayloadHash))
                throw new InvalidDataException("World checkpoint payload SHA-256 validation failed.");
            source.Position = CheckedAbsolute(checkpointStart, header.TotalLength);
            return loaded;
        }
        catch (Exception readFailure)
        {
            if (loaded is not null)
            {
                WorldSerializer.RethrowAfterTemporaryWorldFailure(
                    readFailure,
                    loaded.Dispose);
            }
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
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], ExactRegistryFlag);
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
            throw new InvalidDataException(
                "Unsupported World checkpoint envelope; only current SEWCP003 is accepted.");
        }
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        if (version != Version)
            throw new InvalidDataException($"Unsupported World checkpoint version {version}.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]) != HeaderSize)
            throw new InvalidDataException("World checkpoint header size is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]) != ExactRegistryFlag)
            throw new InvalidDataException("World checkpoint flags are invalid.");

        Digest256 expectedHeaderHash = Digest256.ReadPrefix24(bytes[HeaderHashOffset..]);
        Digest256 actualHeaderHash = ComputeHash(bytes[..HeaderHashOffset]);
        if (!actualHeaderHash.FixedTimePrefix24Equals(expectedHeaderHash))
            throw new InvalidDataException("World checkpoint header authentication failed.");

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
        Span<byte> count = stackalloc byte[4];
        Span<byte> record = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(count, checked((uint)registry.RuntimeTypes.Length));
        hash.AppendData(count);
        for (int i = 0; i < registry.RuntimeTypes.Length; i++)
        {
            SerializationTypeEntry entry = registry.RuntimeTypes[i].Entry;
            record.Clear();
            record[0] = (byte)'C';
            record[1] = (byte)entry.Kind;
            record[2] = (byte)entry.CodecKind;
            if (!entry.TypeKey.StableId.TryWriteBytes(record[8..24]))
                throw new InvalidOperationException("Could not encode a component stable ID.");
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..32], entry.TypeKey.SchemaFingerprint);
            hash.AppendData(record);
            AppendStableName(hash, entry.TypeKey.StableName, count);
        }

        ReadOnlySpan<TopologySerializationRuntime> topologyRuntimes = registry.TopologyRuntimes;
        BinaryPrimitives.WriteUInt32LittleEndian(count, checked((uint)topologyRuntimes.Length));
        hash.AppendData(count);
        for (int i = 0; i < topologyRuntimes.Length; i++)
        {
            TopologySerializationRuntime runtime = topologyRuntimes[i];
            record.Clear();
            record[0] = (byte)'T';
            record[1] = (byte)runtime.Kind;
            if (!runtime.TypeKey.StableId.TryWriteBytes(record[8..24]))
                throw new InvalidOperationException("Could not encode a topology stable ID.");
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..32], runtime.TypeKey.SchemaFingerprint);
            hash.AppendData(record);
            AppendStableName(hash, runtime.TypeKey.StableName, count);
        }
        return Digest256.Finish(hash);
    }

    private static void AppendStableName(
        IncrementalHash hash,
        string stableName,
        Span<byte> length)
    {
        byte[] utf8 = SerializationBinary.StrictUtf8.GetBytes(stableName);
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)utf8.Length));
        hash.AppendData(length);
        hash.AppendData(utf8);
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
