using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SomeEngine.Graphics;

internal readonly record struct PipelineCacheLimits(
    int MaximumEntryCount,
    int MaximumByteCount,
    int MaximumDecodedByteCount)
{
    internal static PipelineCacheLimits FromPolicy(
        int maximumEntryCount,
        int maximumByteCount,
        int maximumDecodedByteCount) =>
        new(
            maximumEntryCount == 0
                ? PipelineCacheEnvelope.HardEntryCountLimit
                : Math.Min(maximumEntryCount, PipelineCacheEnvelope.HardEntryCountLimit),
            maximumByteCount == 0 ? int.MaxValue : maximumByteCount,
            maximumDecodedByteCount == 0 ? int.MaxValue : maximumDecodedByteCount);
}

internal readonly record struct PipelineCacheEntry(
    ulong Backend,
    byte Family,
    byte[] Key,
    byte[] Compatibility,
    byte[] Payload);

internal sealed class ParsedPipelineCache
{
    private readonly PipelineCacheEntry[] _entries;

    internal ParsedPipelineCache(PipelineCacheEntry[] entries) => _entries = entries;

    internal ReadOnlySpan<PipelineCacheEntry> Entries => _entries;

    internal bool TryGetCompatibleEntry(
        ulong backend,
        byte family,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> compatibility,
        out PipelineCacheEntry entry)
    {
        foreach (PipelineCacheEntry candidate in _entries)
        {
            if (candidate.Backend == backend &&
                candidate.Family == family &&
                candidate.Key.AsSpan().SequenceEqual(key) &&
                candidate.Compatibility.AsSpan().SequenceEqual(compatibility))
            {
                entry = candidate;
                return true;
            }
        }
        entry = default;
        return false;
    }
}

internal static class PipelineCacheEnvelope
{
    internal const uint SchemaVersion = 3;
    internal const int HardEntryCountLimit = 1_000_000;
    internal const int EmptyEnvelopeByteCount = 48;
    internal const int EntryFixedByteCount = 109;
    internal const int HashByteCount = 32;
    private const int CancellationChunkByteCount = 64 * 1024;

    private static ReadOnlySpan<byte> Magic => "SERHIC01"u8;

    internal static ParsedPipelineCache Parse(
        ReadOnlySpan<byte> data,
        PipelineCacheLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateLimits(limits);
            if (data.IsEmpty)
                return new ParsedPipelineCache([]);
            if (data.Length < EmptyEnvelopeByteCount)
                throw new InvalidDataException("The pipeline-cache envelope is truncated.");
            if (data.Length > limits.MaximumByteCount)
            {
                throw new ArgumentException(
                    "The complete pipeline-cache envelope exceeds the configured serialized-byte limit.",
                    nameof(data));
            }

            ReadOnlySpan<byte> body = data[..^HashByteCount];
            Span<byte> actualEnvelopeHash = stackalloc byte[HashByteCount];
            ComputeSha256(body, actualEnvelopeHash, cancellationToken);
            if (!actualEnvelopeHash.SequenceEqual(data[^HashByteCount..]))
                throw new InvalidDataException("The pipeline-cache envelope checksum is invalid.");

            int offset = 0;
            if (!ReadSpan(body, ref offset, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("The pipeline-cache magic is invalid.");
            if (ReadUInt32(body, ref offset) != SchemaVersion)
                throw new InvalidDataException("The pipeline-cache schema is unsupported.");
            uint count = ReadUInt32(body, ref offset);
            if (count > HardEntryCountLimit)
                throw new InvalidDataException("The pipeline-cache entry count is invalid.");
            if (count > limits.MaximumEntryCount)
            {
                throw new ArgumentException(
                    "The complete pipeline-cache envelope exceeds the configured entry-count limit.",
                    nameof(data));
            }

            var entries = new PipelineCacheEntry[checked((int)count)];
            long decodedByteCount = 0;
            Span<byte> actualHash = stackalloc byte[HashByteCount];
            for (int index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong backend = ReadUInt64(body, ref offset);
                byte family = ReadByte(body, ref offset);
                byte[] key = CopyBytes(ReadSpan(body, ref offset, HashByteCount), cancellationToken);
                byte[] compatibility = CopyBytes(
                    ReadSpan(body, ref offset, HashByteCount),
                    cancellationToken);
                int payloadLength = checked((int)ReadUInt32(body, ref offset));
                decodedByteCount = checked(decodedByteCount + payloadLength);
                if (decodedByteCount > limits.MaximumDecodedByteCount)
                {
                    throw new ArgumentException(
                        "The complete pipeline-cache envelope exceeds the configured decoded-byte limit.",
                        nameof(data));
                }
                byte[] payload = CopyBytes(
                    ReadSpan(body, ref offset, payloadLength),
                    cancellationToken);
                ReadOnlySpan<byte> expectedHash = ReadSpan(body, ref offset, HashByteCount);
                ComputeSha256(payload, actualHash, cancellationToken);
                if (!actualHash.SequenceEqual(expectedHash))
                    throw new InvalidDataException("A pipeline-cache section checksum is invalid.");

                var entry = new PipelineCacheEntry(
                    backend,
                    family,
                    key,
                    compatibility,
                    payload);
                if (index > 0)
                {
                    int comparison = Compare(entries[index - 1], entry);
                    if (comparison == 0)
                        throw new InvalidDataException("The pipeline-cache envelope contains a duplicate section.");
                    if (comparison > 0)
                        throw new InvalidDataException("The pipeline-cache sections are not in canonical key order.");
                }
                entries[index] = entry;
            }
            if (offset != body.Length)
                throw new InvalidDataException("The pipeline-cache envelope has trailing bytes.");
            cancellationToken.ThrowIfCancellationRequested();
            return new ParsedPipelineCache(entries);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or EndOfStreamException or OverflowException)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "The pipeline-cache envelope is corrupt.",
                innerException: exception);
        }
    }

    internal static byte[] Serialize(
        ReadOnlySpan<PipelineCacheEntry> entries,
        PipelineCacheLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLimits(limits);
        if (entries.Length > limits.MaximumEntryCount || entries.Length > HardEntryCountLimit)
            throw new ArgumentException("The pipeline-cache entries exceed the configured entry-count limit.", nameof(entries));

        var ordered = entries.ToArray();
        Array.Sort(ordered, static (left, right) => Compare(left, right));
        long decodedByteCount = 0;
        long wireByteCount = EmptyEnvelopeByteCount;
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(ordered[index]);
            if (index > 0 && Compare(ordered[index - 1], ordered[index]) == 0)
                throw new ArgumentException("The pipeline-cache entries contain a duplicate key.", nameof(entries));
            decodedByteCount = checked(decodedByteCount + ordered[index].Payload.Length);
            wireByteCount = checked(wireByteCount + GetEntryWireByteCount(ordered[index].Payload.Length));
        }
        if (decodedByteCount > limits.MaximumDecodedByteCount)
            throw new ArgumentException("The pipeline-cache entries exceed the configured decoded-byte limit.", nameof(entries));
        if (wireByteCount > limits.MaximumByteCount || wireByteCount > int.MaxValue)
            throw new ArgumentException("The pipeline-cache entries exceed the configured serialized-byte limit.", nameof(entries));

        var result = new byte[checked((int)wireByteCount)];
        int offset = 0;
        Magic.CopyTo(result);
        offset += Magic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), SchemaVersion);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), checked((uint)ordered.Length));
        offset += sizeof(uint);
        foreach (PipelineCacheEntry entry in ordered)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset), entry.Backend);
            offset += sizeof(ulong);
            result[offset++] = entry.Family;
            entry.Key.CopyTo(result, offset);
            offset += HashByteCount;
            entry.Compatibility.CopyTo(result, offset);
            offset += HashByteCount;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), checked((uint)entry.Payload.Length));
            offset += sizeof(uint);
            CopyWithCancellation(entry.Payload, result.AsSpan(offset, entry.Payload.Length), cancellationToken);
            offset += entry.Payload.Length;
            ComputeSha256(entry.Payload, result.AsSpan(offset, HashByteCount), cancellationToken);
            offset += HashByteCount;
        }
        ComputeSha256(result.AsSpan(0, offset), result.AsSpan(offset, HashByteCount), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static PipelineCacheEntry[] Merge(
        ReadOnlySpan<PipelineCacheEntry> destination,
        ReadOnlySpan<PipelineCacheEntry> source,
        PipelineCacheLimits limits,
        CancellationToken cancellationToken)
    {
        var entries = new SortedDictionary<PipelineCacheEntryKey, PipelineCacheEntry>();
        Add(destination);
        Add(source);
        PipelineCacheEntry[] result = entries.Values.ToArray();
        _ = Serialize(result, limits, cancellationToken);
        return result;

        void Add(ReadOnlySpan<PipelineCacheEntry> candidates)
        {
            foreach (PipelineCacheEntry candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntry(candidate);
                var key = new PipelineCacheEntryKey(candidate);
                if (!entries.TryGetValue(key, out PipelineCacheEntry existing) ||
                    CompareBytes(candidate.Payload, existing.Payload, cancellationToken) < 0)
                    entries[key] = Clone(candidate, cancellationToken);
            }
        }
    }

    internal static int GetEntryWireByteCount(int payloadByteCount) =>
        checked(EntryFixedByteCount + payloadByteCount);

    internal static byte[] CopyBytes(
        ReadOnlySpan<byte> source,
        CancellationToken cancellationToken)
    {
        var result = new byte[source.Length];
        CopyWithCancellation(source, result, cancellationToken);
        return result;
    }

    internal static void ComputeSha256(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        if (destination.Length < HashByteCount)
            throw new ArgumentException("A SHA-256 destination must contain at least 32 bytes.", nameof(destination));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(CancellationChunkByteCount, source.Length - offset);
            hash.AppendData(source.Slice(offset, count));
            offset += count;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!hash.TryGetHashAndReset(destination, out int written) || written != HashByteCount)
            throw new CryptographicException("SHA-256 did not produce a 32-byte digest.");
    }

    private static PipelineCacheEntry Clone(
        PipelineCacheEntry entry,
        CancellationToken cancellationToken) =>
        new(
            entry.Backend,
            entry.Family,
            CopyBytes(entry.Key, cancellationToken),
            CopyBytes(entry.Compatibility, cancellationToken),
            CopyBytes(entry.Payload, cancellationToken));

    private static void ValidateLimits(PipelineCacheLimits limits)
    {
        if (limits.MaximumEntryCount < 0 ||
            limits.MaximumByteCount < EmptyEnvelopeByteCount ||
            limits.MaximumDecodedByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private static void ValidateEntry(PipelineCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry.Key);
        ArgumentNullException.ThrowIfNull(entry.Compatibility);
        ArgumentNullException.ThrowIfNull(entry.Payload);
        if (entry.Key.Length != HashByteCount || entry.Compatibility.Length != HashByteCount)
            throw new ArgumentException("Pipeline-cache keys and compatibility identities must be SHA-256 values.");
    }

    private static int Compare(PipelineCacheEntry left, PipelineCacheEntry right)
    {
        int result = left.Backend.CompareTo(right.Backend);
        if (result != 0)
            return result;
        result = left.Family.CompareTo(right.Family);
        if (result != 0)
            return result;
        result = left.Key.AsSpan().SequenceCompareTo(right.Key);
        return result != 0
            ? result
            : left.Compatibility.AsSpan().SequenceCompareTo(right.Compatibility);
    }

    private static int CompareBytes(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right,
        CancellationToken cancellationToken)
    {
        int sharedLength = Math.Min(left.Length, right.Length);
        for (int offset = 0; offset < sharedLength;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(CancellationChunkByteCount, sharedLength - offset);
            int result = left.Slice(offset, count).SequenceCompareTo(right.Slice(offset, count));
            if (result != 0)
                return result;
            offset += count;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static void CopyWithCancellation(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Pipeline-cache copy spans must have equal lengths.");
        for (int offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(CancellationChunkByteCount, source.Length - offset);
            source.Slice(offset, count).CopyTo(destination.Slice(offset, count));
            offset += count;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        if (source.Length - offset < sizeof(uint))
            throw new EndOfStreamException();
        uint result = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += sizeof(uint);
        return result;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        if (source.Length - offset < sizeof(ulong))
            throw new EndOfStreamException();
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        offset += sizeof(ulong);
        return result;
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int offset)
    {
        if ((uint)offset >= (uint)source.Length)
            throw new EndOfStreamException();
        return source[offset++];
    }

    private static ReadOnlySpan<byte> ReadSpan(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        if (length < 0 || source.Length - offset < length)
            throw new EndOfStreamException();
        ReadOnlySpan<byte> result = source.Slice(offset, length);
        offset += length;
        return result;
    }

    private readonly record struct PipelineCacheEntryKey(
        ulong Backend,
        byte Family,
        byte[] Key,
        byte[] Compatibility) : IComparable<PipelineCacheEntryKey>
    {
        internal PipelineCacheEntryKey(PipelineCacheEntry entry)
            : this(entry.Backend, entry.Family, entry.Key, entry.Compatibility)
        {
        }

        public int CompareTo(PipelineCacheEntryKey other)
        {
            int result = Backend.CompareTo(other.Backend);
            if (result != 0)
                return result;
            result = Family.CompareTo(other.Family);
            if (result != 0)
                return result;
            result = Key.AsSpan().SequenceCompareTo(other.Key);
            return result != 0
                ? result
                : Compatibility.AsSpan().SequenceCompareTo(other.Compatibility);
        }
    }
}
