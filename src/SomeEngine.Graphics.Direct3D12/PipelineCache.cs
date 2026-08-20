using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const uint PipelineCacheEnvelopeSchemaVersion = 3;
    private const int PipelineCacheHardEntryCountLimit = 1_000_000;
    private const int PipelineCacheEmptyEnvelopeByteCount = 48;
    private const int PipelineCacheEntryFixedByteCount = 109;
    private const int PipelineCacheHashByteCount = 32;
    private const int PipelineCacheCancellationChunkByteCount = 64 * 1024;
    // Stable little-endian wire identity: ASCII "D3D12" followed by three zero bytes.
    private const ulong D3D12PipelineCacheBackendTag = 0x0000_0032_3144_3344UL;
    private const uint PipelineKeySchemaVersion = 2;
    private const uint D3D12BackendAbiVersion = 1;
    private const uint StateObjectReplaySchemaVersion = 2;
    private const byte SlangTargetSettingsIdentityVersion = 1;
    private const string AgilityPackageVersion = "1.619.5";

    public PipelineCache CreatePipelineCache(
        Device device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePipelineCachePolicy(desc);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache result = new(
            nativeDevice,
            desc.Data,
            desc.MaximumEntryCount,
            desc.MaximumByteCount,
            desc.MaximumDecodedByteCount,
            desc.Label,
            cancellationToken);
        nativeDevice.RegisterChild(result);
        return result;
    }

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] data = RequirePipelineCache(cache).Serialize(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        requiredByteCount = data.Length;
        if (destination.Length < data.Length)
            return false;
        data.CopyTo(destination);
        return true;
    }

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        D3D12PipelineCache nativeDestination = RequirePipelineCache(destination);
        var nativeSources = new D3D12PipelineCache[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nativeSources[index] = RequirePipelineCache(sources[index]);
        }
        nativeDestination.Merge(nativeSources, cancellationToken);
    }

    private static void ValidatePipelineCachePolicy(in PipelineCacheDesc desc)
    {
        if (desc.MaximumEntryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumEntryCount),
                desc.MaximumEntryCount,
                "The maximum pipeline-cache entry count must not be negative.");
        }
        if (desc.MaximumByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumByteCount),
                desc.MaximumByteCount,
                "The maximum serialized pipeline-cache byte count must not be negative.");
        }
        if (desc.MaximumByteCount is > 0 and < PipelineCacheEmptyEnvelopeByteCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumByteCount),
                desc.MaximumByteCount,
                $"The D3D12 pipeline-cache byte limit must be zero or at least " +
                $"{PipelineCacheEmptyEnvelopeByteCount} bytes for an empty envelope.");
        }
        if (desc.MaximumDecodedByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumDecodedByteCount),
                desc.MaximumDecodedByteCount,
                "The maximum decoded pipeline-cache byte count must not be negative.");
        }
    }

    private sealed class D3D12PipelineCache : PipelineCache
    {
        private static ReadOnlySpan<byte> Magic => "SERHIC01"u8;
        private static readonly object MergeGate = new();

        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private readonly int _maximumEntryCount;
        private readonly int _maximumByteCount;
        private readonly int _maximumDecodedByteCount;
        private SortedDictionary<CacheEntryKey, CacheEntry> _entries = [];
        private int _residentWireByteCount = PipelineCacheEmptyEnvelopeByteCount;
        private int _residentDecodedByteCount;
        private int _pipelineCreationReferences = 1;

        internal D3D12PipelineCache(
            D3D12Device device,
            ReadOnlySpan<byte> data,
            int maximumEntryCount,
            int maximumByteCount,
            int maximumDecodedByteCount,
            string? label,
            CancellationToken cancellationToken)
            : base(device, label)
        {
            _device = device;
            _maximumEntryCount = maximumEntryCount == 0
                ? PipelineCacheHardEntryCountLimit
                : Math.Min(maximumEntryCount, PipelineCacheHardEntryCountLimit);
            _maximumByteCount = maximumByteCount == 0 ? int.MaxValue : maximumByteCount;
            _maximumDecodedByteCount = maximumDecodedByteCount == 0
                ? int.MaxValue
                : maximumDecodedByteCount;
            Compatibility = ComputeCompatibility(device, cancellationToken);
            if (!data.IsEmpty)
                Parse(data, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal byte[] Compatibility { get; }

        internal bool TryGet(byte family, ReadOnlySpan<byte> key, out CacheCandidate? candidate)
        {
            CacheEntryKey entryKey = CreateLocalEntryKey(family, key);
            bool found;
            lock (_gate)
            {
                if (_entries.TryGetValue(entryKey, out CacheEntry? result))
                {
                    candidate = new CacheCandidate(this, entryKey, result);
                    found = true;
                }
                else
                {
                    candidate = null;
                    found = false;
                }
            }
            D3D12PipelineCompiler.RecordCacheLookup(found);
            return found;
        }

        internal bool Reject(CacheCandidate candidate)
        {
            if (!ReferenceEquals(candidate.Owner, this))
                return false;
            lock (_gate)
            {
                if (!_entries.TryGetValue(candidate.Key, out CacheEntry? current) ||
                    !ReferenceEquals(current, candidate.Entry))
                {
                    return false;
                }

                try
                {
                    var replacement = new SortedDictionary<CacheEntryKey, CacheEntry>();
                    foreach ((CacheEntryKey key, CacheEntry entry) in _entries)
                    {
                        if (key.CompareTo(candidate.Key) != 0)
                            replacement.Add(key, entry);
                    }
                    int wireByteCount = checked(_residentWireByteCount - candidate.Entry.WireByteCount);
                    int decodedByteCount = checked(_residentDecodedByteCount - candidate.Entry.Payload.Length);
                    _entries = replacement;
                    _residentWireByteCount = wireByteCount;
                    _residentDecodedByteCount = decodedByteCount;
                    return true;
                }
                catch (Exception exception) when (exception is OutOfMemoryException or OverflowException)
                {
                    return false;
                }
            }
        }

        internal CacheAdmission? PrepareAdmission(
            byte family,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> data,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SortedDictionary<CacheEntryKey, CacheEntry> expectedRoot = _entries;
                int wireByteCount;
                long singleEntryEnvelopeByteCount;
                try
                {
                    wireByteCount = checked(PipelineCacheEntryFixedByteCount + data.Length);
                    singleEntryEnvelopeByteCount = checked(
                        (long)PipelineCacheEmptyEnvelopeByteCount + wireByteCount);
                }
                catch (OverflowException)
                {
                    return null;
                }

                if (!CanContain(1, singleEntryEnvelopeByteCount, data.Length))
                    return null;

                byte[] keyCopy = CopyBytes(key, cancellationToken);
                var entryKey = new CacheEntryKey(
                    D3D12PipelineCacheBackendTag,
                    family,
                    keyCopy,
                    Compatibility);
                bool hasExisting = expectedRoot.TryGetValue(entryKey, out CacheEntry? existing);
                long candidateEntryCount = hasExisting
                    ? expectedRoot.Count
                    : (long)expectedRoot.Count + 1;
                long candidateWireByteCount = hasExisting
                    ? (long)_residentWireByteCount - existing!.WireByteCount + wireByteCount
                    : (long)_residentWireByteCount + wireByteCount;
                long candidateDecodedByteCount = hasExisting
                    ? (long)_residentDecodedByteCount - existing!.Payload.Length + data.Length
                    : (long)_residentDecodedByteCount + data.Length;

                if ((hasExisting &&
                        CompareBytes(data, existing!.Payload, cancellationToken) >= 0) ||
                    !CanContain(
                        candidateEntryCount,
                        candidateWireByteCount,
                        candidateDecodedByteCount))
                {
                    return null;
                }

                byte[] payloadCopy = CopyBytes(data, cancellationToken);
                var candidate = new SortedDictionary<CacheEntryKey, CacheEntry>();
                foreach ((CacheEntryKey existingKey, CacheEntry existingEntry) in expectedRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidate.Add(existingKey, existingEntry);
                }
                candidate[entryKey] = new CacheEntry(payloadCopy, wireByteCount);
                cancellationToken.ThrowIfCancellationRequested();
                return new CacheAdmission(
                    this,
                    expectedRoot,
                    candidate,
                    checked((int)candidateWireByteCount),
                    checked((int)candidateDecodedByteCount));
            }
        }

        internal bool CommitAdmission(CacheAdmission? admission)
        {
            if (admission is null || !ReferenceEquals(admission.Owner, this))
                return false;
            lock (_gate)
            {
                if (!ReferenceEquals(_entries, admission.ExpectedRoot))
                    return false;
                _entries = admission.Entries;
                _residentWireByteCount = admission.WireByteCount;
                _residentDecodedByteCount = admission.DecodedByteCount;
                return true;
            }
        }

        internal void Store(byte family, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
        {
            CacheAdmission? admission = PrepareAdmission(
                family,
                key,
                data,
                CancellationToken.None);
            CommitAdmission(admission);
        }

        internal void Merge(
            ReadOnlySpan<D3D12PipelineCache> sources,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (MergeGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    var candidate = new SortedDictionary<CacheEntryKey, CacheEntry>();
                    foreach ((CacheEntryKey key, CacheEntry entry) in _entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        candidate.Add(key, entry);
                    }
                    long candidateWireByteCount = _residentWireByteCount;
                    long candidateDecodedByteCount = _residentDecodedByteCount;
                    for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        D3D12PipelineCache source = sources[sourceIndex];
                        lock (source._gate)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            source.ThrowIfDisposed();
                            foreach ((CacheEntryKey key, CacheEntry entry) in source._entries)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                bool hasExisting = candidate.TryGetValue(
                                    key,
                                    out CacheEntry? existing);
                                if (hasExisting &&
                                    CompareBytes(
                                        entry.Payload,
                                        existing!.Payload,
                                        cancellationToken) >= 0)
                                {
                                    continue;
                                }

                                long prospectiveEntryCount = hasExisting
                                    ? candidate.Count
                                    : (long)candidate.Count + 1;
                                if (!hasExisting && !CanContainEntryCount(prospectiveEntryCount))
                                {
                                    throw new ArgumentException(
                                        "The complete pipeline-cache union exceeds the destination cache policy.",
                                        nameof(sources));
                                }
                                long prospectiveWireByteCount = hasExisting
                                    ? candidateWireByteCount - existing!.WireByteCount +
                                        entry.WireByteCount
                                    : candidateWireByteCount + entry.WireByteCount;
                                long prospectiveDecodedByteCount = hasExisting
                                    ? candidateDecodedByteCount - existing!.Payload.Length +
                                        entry.Payload.Length
                                    : candidateDecodedByteCount + entry.Payload.Length;
                                candidate[key] = entry;
                                candidateWireByteCount = prospectiveWireByteCount;
                                candidateDecodedByteCount = prospectiveDecodedByteCount;
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanContain(
                        candidate.Count,
                        candidateWireByteCount,
                        candidateDecodedByteCount))
                    {
                        throw new ArgumentException(
                            "The complete pipeline-cache union exceeds the destination cache policy.",
                            nameof(sources));
                    }
                    _entries = candidate;
                    _residentWireByteCount = checked((int)candidateWireByteCount);
                    _residentDecodedByteCount = checked((int)candidateDecodedByteCount);
                }
            }
        }

        internal byte[] Serialize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                var result = new byte[_residentWireByteCount];
                int offset = 0;
                Magic.CopyTo(result.AsSpan(offset, Magic.Length));
                offset += Magic.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result.AsSpan(offset, sizeof(uint)),
                    PipelineCacheEnvelopeSchemaVersion);
                offset += sizeof(uint);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    result.AsSpan(offset, sizeof(uint)),
                    checked((uint)_entries.Count));
                offset += sizeof(uint);

                foreach ((CacheEntryKey key, CacheEntry entry) in _entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        result.AsSpan(offset, sizeof(ulong)),
                        key.Backend);
                    offset += sizeof(ulong);
                    result[offset++] = key.Family;
                    key.Key.CopyTo(result, offset);
                    offset += key.Key.Length;
                    key.Compatibility.CopyTo(result, offset);
                    offset += key.Compatibility.Length;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result.AsSpan(offset, sizeof(uint)),
                        checked((uint)entry.Payload.Length));
                    offset += sizeof(uint);
                    CopyWithCancellation(
                        entry.Payload,
                        result.AsSpan(offset, entry.Payload.Length),
                        cancellationToken);
                    offset += entry.Payload.Length;
                    ComputeSha256(
                        entry.Payload,
                        result.AsSpan(offset, PipelineCacheHashByteCount),
                        cancellationToken);
                    offset += PipelineCacheHashByteCount;
                }

                if (offset != result.Length - PipelineCacheHashByteCount)
                {
                    throw new InvalidOperationException(
                        "The pipeline-cache resident wire-byte count is inconsistent.");
                }
                ComputeSha256(
                    result.AsSpan(0, offset),
                    result.AsSpan(offset, PipelineCacheHashByteCount),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }

        internal void RetainForPipelineCreation()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _pipelineCreationReferences = checked(_pipelineCreationReferences + 1);
            }
        }

        internal void ReleasePipelineCreationUse()
        {
            bool release;
            lock (_gate)
            {
                int references = --_pipelineCreationReferences;
                if (references < 0)
                    throw new InvalidOperationException("PipelineCache physical references underflowed.");
                release = references == 0;
                if (release)
                {
                    _entries.Clear();
                    _residentWireByteCount = PipelineCacheEmptyEnvelopeByteCount;
                    _residentDecodedByteCount = 0;
                }
            }
            if (release)
                _device.UnregisterChild(this);
        }

        internal override void Release(bool fromParent) => ReleasePipelineCreationUse();

        private void Parse(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.Length < PipelineCacheEmptyEnvelopeByteCount)
                    throw new InvalidDataException("The pipeline-cache envelope is truncated.");
                if (data.Length > _maximumByteCount)
                {
                    throw new ArgumentException(
                        "The complete pipeline-cache envelope exceeds the configured serialized-byte limit.",
                        "data");
                }

                ReadOnlySpan<byte> body = data[..^PipelineCacheHashByteCount];
                Span<byte> envelopeHash = stackalloc byte[PipelineCacheHashByteCount];
                ComputeSha256(body, envelopeHash, cancellationToken);
                if (!envelopeHash.SequenceEqual(data[^PipelineCacheHashByteCount..]))
                    throw new InvalidDataException("The pipeline-cache envelope checksum is invalid.");
                int offset = 0;
                if (!body[..Magic.Length].SequenceEqual(Magic))
                    throw new InvalidDataException("The pipeline-cache magic is invalid.");
                offset += Magic.Length;
                uint version = ReadUInt32(body, ref offset);
                if (version != PipelineCacheEnvelopeSchemaVersion)
                    throw new InvalidDataException("The pipeline-cache schema is unsupported.");
                uint count = ReadUInt32(body, ref offset);
                if (count > PipelineCacheHardEntryCountLimit)
                    throw new InvalidDataException("The pipeline-cache entry count is invalid.");
                long decodedByteCount = 0;
                bool hasPrevious = false;
                ulong previousBackend = 0;
                byte previousFamily = 0;
                Span<byte> previousKey = stackalloc byte[PipelineCacheHashByteCount];
                Span<byte> previousCompatibility = stackalloc byte[PipelineCacheHashByteCount];
                for (uint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CacheEnvelopeSection section = ReadCacheSection(
                        body,
                        ref offset,
                        verifyChecksum: true,
                        cancellationToken);
                    if (hasPrevious)
                    {
                        int comparison = CompareEntryKeys(
                            previousBackend,
                            previousFamily,
                            previousKey,
                            previousCompatibility,
                            section.Backend,
                            section.Family,
                            section.Key,
                            section.Compatibility);
                        if (comparison == 0)
                        {
                            throw new InvalidDataException(
                                "The pipeline-cache envelope contains a duplicate section.");
                        }
                        if (comparison > 0)
                        {
                            throw new InvalidDataException(
                                "The pipeline-cache sections are not in canonical key order.");
                        }
                    }
                    hasPrevious = true;
                    previousBackend = section.Backend;
                    previousFamily = section.Family;
                    section.Key.CopyTo(previousKey);
                    section.Compatibility.CopyTo(previousCompatibility);
                    decodedByteCount = checked(decodedByteCount + section.Payload.Length);
                }
                if (offset != body.Length)
                    throw new InvalidDataException("The pipeline-cache envelope has trailing bytes.");

                if (count > _maximumEntryCount)
                {
                    throw new ArgumentException(
                        "The complete pipeline-cache envelope exceeds the configured entry-count limit.",
                        "data");
                }
                if (decodedByteCount > _maximumDecodedByteCount)
                {
                    throw new ArgumentException(
                        "The complete pipeline-cache envelope exceeds the configured decoded-byte limit.",
                        "data");
                }

                var candidate = new SortedDictionary<CacheEntryKey, CacheEntry>();
                offset = Magic.Length + sizeof(uint) + sizeof(uint);
                for (uint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CacheEnvelopeSection section = ReadCacheSection(
                        body,
                        ref offset,
                        verifyChecksum: false,
                        cancellationToken);
                    byte[] payload = CopyBytes(section.Payload, cancellationToken);
                    candidate.Add(
                        new CacheEntryKey(
                            section.Backend,
                            section.Family,
                            section.Key.ToArray(),
                            section.Compatibility.ToArray()),
                        new CacheEntry(payload, section.WireByteCount));
                }
                cancellationToken.ThrowIfCancellationRequested();
                _entries = candidate;
                _residentWireByteCount = data.Length;
                _residentDecodedByteCount = checked((int)decodedByteCount);
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

        private CacheEntryKey CreateLocalEntryKey(byte family, ReadOnlySpan<byte> key) =>
            new(
                D3D12PipelineCacheBackendTag,
                family,
                key.ToArray(),
                Compatibility);

        private bool CanContainEntryCount(long entryCount) =>
            entryCount <= _maximumEntryCount;

        private bool CanContain(long entryCount, long wireByteCount, long decodedByteCount) =>
            CanContainEntryCount(entryCount) &&
            wireByteCount <= _maximumByteCount &&
            decodedByteCount <= _maximumDecodedByteCount;

        private static byte[] ComputeCompatibility(
            D3D12Device device,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(PipelineCacheEnvelopeSchemaVersion);
                writer.Write(PipelineKeySchemaVersion);
                writer.Write(D3D12BackendAbiVersion);
                writer.Write(RootSignatureSchemaVersion);
                writer.Write(StateObjectReplaySchemaVersion);
                writer.Write(AgilitySdkVersion);
                WriteCanonicalString(writer, AgilityPackageVersion);
                WriteCanonicalString(writer, SlangToolchainIdentity.Version);

                AdapterInfo adapter = device.Adapter;
                writer.Write(adapter.Id.Low);
                writer.Write(adapter.Id.High);
                writer.Write((byte)adapter.Type);
                WriteCanonicalString(writer, adapter.Name);
                writer.Write(adapter.VendorId);
                writer.Write(adapter.DeviceId);
                writer.Write(adapter.DedicatedVideoMemory);
                writer.Write(adapter.DedicatedSystemMemory);
                writer.Write(adapter.SharedSystemMemory);
                WriteCanonicalString(writer, adapter.DriverVersion);
                writer.Write(adapter.HardwareAccelerated);
                writer.Write(device.EnabledNodeMask);

                DeviceCapabilities capabilities = device.Capabilities;
                WriteDeviceLimits(writer, capabilities.Limits);
                writer.Write(capabilities.SupportsBundles);
                writer.Write(capabilities.SupportsPipelineStatistics);
                writer.Write(capabilities.SupportsStreamOutputStatistics);
                writer.Write((ushort)capabilities.SupportedDynamicStates);
                WriteFormatSupport(writer, capabilities.Formats, cancellationToken);
                writer.Write(device.FeatureSupport.NodeCount);
                writer.Write(device.EnhancedBarriers);

                WriteDeviceCapabilities(device, writer, cancellationToken);
            }

            var result = new byte[PipelineCacheHashByteCount];
            ComputeSha256(
                stream.GetBuffer().AsSpan(0, checked((int)stream.Length)),
                result,
                cancellationToken);
            return result;
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
                int byteCount = Math.Min(
                    PipelineCacheCancellationChunkByteCount,
                    sharedLength - offset);
                int comparison = left.Slice(offset, byteCount).SequenceCompareTo(
                    right.Slice(offset, byteCount));
                if (comparison != 0)
                    return comparison;
                offset += byteCount;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return left.Length.CompareTo(right.Length);
        }

        private static int CompareEntryKeys(
            ulong leftBackend,
            byte leftFamily,
            ReadOnlySpan<byte> leftKey,
            ReadOnlySpan<byte> leftCompatibility,
            ulong rightBackend,
            byte rightFamily,
            ReadOnlySpan<byte> rightKey,
            ReadOnlySpan<byte> rightCompatibility)
        {
            int result = leftBackend.CompareTo(rightBackend);
            if (result != 0)
                return result;
            result = leftFamily.CompareTo(rightFamily);
            if (result != 0)
                return result;
            result = leftKey.SequenceCompareTo(rightKey);
            return result != 0
                ? result
                : leftCompatibility.SequenceCompareTo(rightCompatibility);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            if (source.Length - offset < 4)
                throw new EndOfStreamException();
            uint result = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            offset += 4;
            return result;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
        {
            if (source.Length - offset < 8)
                throw new EndOfStreamException();
            ulong result = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
            offset += 8;
            return result;
        }

        private static byte ReadByte(ReadOnlySpan<byte> source, ref int offset)
        {
            if ((uint)offset >= (uint)source.Length)
                throw new EndOfStreamException();
            return source[offset++];
        }

        private static ReadOnlySpan<byte> ReadSpan(
            ReadOnlySpan<byte> source,
            ref int offset,
            int length)
        {
            if (length < 0 || source.Length - offset < length)
                throw new EndOfStreamException();
            ReadOnlySpan<byte> result = source.Slice(offset, length);
            offset += length;
            return result;
        }

        private static CacheEnvelopeSection ReadCacheSection(
            ReadOnlySpan<byte> body,
            ref int offset,
            bool verifyChecksum,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong backend = ReadUInt64(body, ref offset);
            byte family = ReadByte(body, ref offset);
            ReadOnlySpan<byte> key = ReadSpan(
                body,
                ref offset,
                PipelineCacheHashByteCount);
            ReadOnlySpan<byte> compatibility = ReadSpan(
                body,
                ref offset,
                PipelineCacheHashByteCount);
            int payloadLength = checked((int)ReadUInt32(body, ref offset));
            int wireByteCount = checked(PipelineCacheEntryFixedByteCount + payloadLength);
            ReadOnlySpan<byte> payload = ReadSpan(body, ref offset, payloadLength);
            ReadOnlySpan<byte> checksum = ReadSpan(
                body,
                ref offset,
                PipelineCacheHashByteCount);
            if (verifyChecksum)
            {
                Span<byte> actualChecksum = stackalloc byte[PipelineCacheHashByteCount];
                ComputeSha256(payload, actualChecksum, cancellationToken);
                if (!actualChecksum.SequenceEqual(checksum))
                {
                    throw new InvalidDataException(
                        "A pipeline-cache section checksum is invalid.");
                }
            }
            return new CacheEnvelopeSection(
                backend,
                family,
                key,
                compatibility,
                payload,
                wireByteCount);
        }

        private static byte[] CopyBytes(
            ReadOnlySpan<byte> source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new byte[source.Length];
            CopyWithCancellation(source, result, cancellationToken);
            return result;
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
                int byteCount = Math.Min(
                    PipelineCacheCancellationChunkByteCount,
                    source.Length - offset);
                source.Slice(offset, byteCount).CopyTo(destination.Slice(offset, byteCount));
                offset += byteCount;
            }
        }

        private static void ComputeSha256(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            CancellationToken cancellationToken)
        {
            if (destination.Length < PipelineCacheHashByteCount)
                throw new ArgumentException("A SHA-256 destination must contain at least 32 bytes.");

            Span<uint> state = stackalloc uint[8]
            {
                0x6A09E667u,
                0xBB67AE85u,
                0x3C6EF372u,
                0xA54FF53Au,
                0x510E527Fu,
                0x9B05688Cu,
                0x1F83D9ABu,
                0x5BE0CD19u,
            };
            Span<uint> schedule = stackalloc uint[64];
            int offset = 0;
            while (source.Length - offset >= 64)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransformSha256(source.Slice(offset, 64), state, schedule);
                offset += 64;
            }

            Span<byte> tail = stackalloc byte[128];
            tail.Clear();
            ReadOnlySpan<byte> remaining = source[offset..];
            remaining.CopyTo(tail);
            tail[remaining.Length] = 0x80;
            int paddedByteCount = remaining.Length <= 55 ? 64 : 128;
            BinaryPrimitives.WriteUInt64BigEndian(
                tail.Slice(paddedByteCount - sizeof(ulong), sizeof(ulong)),
                checked((ulong)source.Length * 8UL));
            for (int tailOffset = 0; tailOffset < paddedByteCount; tailOffset += 64)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransformSha256(tail.Slice(tailOffset, 64), state, schedule);
            }

            for (int index = 0; index < state.Length; index++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    destination.Slice(index * sizeof(uint), sizeof(uint)),
                    state[index]);
            }
        }

        private static void TransformSha256(
            ReadOnlySpan<byte> block,
            Span<uint> state,
            Span<uint> schedule)
        {
            for (int index = 0; index < 16; index++)
            {
                schedule[index] = BinaryPrimitives.ReadUInt32BigEndian(
                    block.Slice(index * sizeof(uint), sizeof(uint)));
            }

            unchecked
            {
                for (int index = 16; index < schedule.Length; index++)
                {
                    uint value15 = schedule[index - 15];
                    uint sigma0 = BitOperations.RotateRight(value15, 7) ^
                        BitOperations.RotateRight(value15, 18) ^
                        (value15 >> 3);
                    uint value2 = schedule[index - 2];
                    uint sigma1 = BitOperations.RotateRight(value2, 17) ^
                        BitOperations.RotateRight(value2, 19) ^
                        (value2 >> 10);
                    schedule[index] = schedule[index - 16] + sigma0 +
                        schedule[index - 7] + sigma1;
                }

                uint a = state[0];
                uint b = state[1];
                uint c = state[2];
                uint d = state[3];
                uint e = state[4];
                uint f = state[5];
                uint g = state[6];
                uint h = state[7];
                ReadOnlySpan<uint> constants = Sha256RoundConstants;
                for (int index = 0; index < schedule.Length; index++)
                {
                    uint sum1 = BitOperations.RotateRight(e, 6) ^
                        BitOperations.RotateRight(e, 11) ^
                        BitOperations.RotateRight(e, 25);
                    uint choose = (e & f) ^ (~e & g);
                    uint temporary1 = h + sum1 + choose + constants[index] + schedule[index];
                    uint sum0 = BitOperations.RotateRight(a, 2) ^
                        BitOperations.RotateRight(a, 13) ^
                        BitOperations.RotateRight(a, 22);
                    uint majority = (a & b) ^ (a & c) ^ (b & c);
                    uint temporary2 = sum0 + majority;
                    h = g;
                    g = f;
                    f = e;
                    e = d + temporary1;
                    d = c;
                    c = b;
                    b = a;
                    a = temporary1 + temporary2;
                }

                state[0] += a;
                state[1] += b;
                state[2] += c;
                state[3] += d;
                state[4] += e;
                state[5] += f;
                state[6] += g;
                state[7] += h;
            }
        }

        private static ReadOnlySpan<uint> Sha256RoundConstants =>
        [
            0x428A2F98u, 0x71374491u, 0xB5C0FBCFu, 0xE9B5DBA5u,
            0x3956C25Bu, 0x59F111F1u, 0x923F82A4u, 0xAB1C5ED5u,
            0xD807AA98u, 0x12835B01u, 0x243185BEu, 0x550C7DC3u,
            0x72BE5D74u, 0x80DEB1FEu, 0x9BDC06A7u, 0xC19BF174u,
            0xE49B69C1u, 0xEFBE4786u, 0x0FC19DC6u, 0x240CA1CCu,
            0x2DE92C6Fu, 0x4A7484AAu, 0x5CB0A9DCu, 0x76F988DAu,
            0x983E5152u, 0xA831C66Du, 0xB00327C8u, 0xBF597FC7u,
            0xC6E00BF3u, 0xD5A79147u, 0x06CA6351u, 0x14292967u,
            0x27B70A85u, 0x2E1B2138u, 0x4D2C6DFCu, 0x53380D13u,
            0x650A7354u, 0x766A0ABBu, 0x81C2C92Eu, 0x92722C85u,
            0xA2BFE8A1u, 0xA81A664Bu, 0xC24B8B70u, 0xC76C51A3u,
            0xD192E819u, 0xD6990624u, 0xF40E3585u, 0x106AA070u,
            0x19A4C116u, 0x1E376C08u, 0x2748774Cu, 0x34B0BCB5u,
            0x391C0CB3u, 0x4ED8AA4Au, 0x5B9CCA4Fu, 0x682E6FF3u,
            0x748F82EEu, 0x78A5636Fu, 0x84C87814u, 0x8CC70208u,
            0x90BEFFFAu, 0xA4506CEBu, 0xBEF9A3F7u, 0xC67178F2u,
        ];

        internal sealed class CacheEntry
        {
            internal CacheEntry(byte[] payload, int wireByteCount)
            {
                Payload = payload;
                WireByteCount = wireByteCount;
            }

            internal byte[] Payload { get; }
            internal int WireByteCount { get; }
        }

        internal readonly struct CacheCandidate
        {
            internal CacheCandidate(
                D3D12PipelineCache owner,
                CacheEntryKey key,
                CacheEntry entry)
            {
                Owner = owner;
                Key = key;
                Entry = entry;
            }

            internal D3D12PipelineCache Owner { get; }
            internal CacheEntryKey Key { get; }
            internal CacheEntry Entry { get; }
            internal byte[] Payload => Entry.Payload;
        }

        internal sealed class CacheAdmission
        {
            internal CacheAdmission(
                D3D12PipelineCache owner,
                SortedDictionary<CacheEntryKey, CacheEntry> expectedRoot,
                SortedDictionary<CacheEntryKey, CacheEntry> entries,
                int wireByteCount,
                int decodedByteCount)
            {
                Owner = owner;
                ExpectedRoot = expectedRoot;
                Entries = entries;
                WireByteCount = wireByteCount;
                DecodedByteCount = decodedByteCount;
            }

            internal readonly D3D12PipelineCache Owner;
            internal readonly SortedDictionary<CacheEntryKey, CacheEntry> ExpectedRoot;
            internal readonly SortedDictionary<CacheEntryKey, CacheEntry> Entries;
            internal readonly int WireByteCount;
            internal readonly int DecodedByteCount;
        }

        internal readonly record struct CacheEntryKey(
            ulong Backend,
            byte Family,
            byte[] Key,
            byte[] Compatibility) : IComparable<CacheEntryKey>
        {
            public int CompareTo(CacheEntryKey other) => CompareEntryKeys(
                Backend,
                Family,
                Key,
                Compatibility,
                other.Backend,
                other.Family,
                other.Key,
                other.Compatibility);
        }

        private readonly ref struct CacheEnvelopeSection
        {
            internal CacheEnvelopeSection(
                ulong backend,
                byte family,
                ReadOnlySpan<byte> key,
                ReadOnlySpan<byte> compatibility,
                ReadOnlySpan<byte> payload,
                int wireByteCount)
            {
                Backend = backend;
                Family = family;
                Key = key;
                Compatibility = compatibility;
                Payload = payload;
                WireByteCount = wireByteCount;
            }

            internal ulong Backend { get; }
            internal byte Family { get; }
            internal ReadOnlySpan<byte> Key { get; }
            internal ReadOnlySpan<byte> Compatibility { get; }
            internal ReadOnlySpan<byte> Payload { get; }
            internal int WireByteCount { get; }
        }
    }

    private static void WriteDeviceCapabilities(
        D3D12Device device,
        BinaryWriter writer,
        CancellationToken cancellationToken)
    {
        writer.Write(device.TryGetCapability(out Presentation? _));
        WriteSparseCapability(device, writer, cancellationToken);
        WriteSamplerFeedbackCapability(device, writer, cancellationToken);
        WriteResidencyCapability(device, writer);
        WriteRayTracingCapability(device, writer);
        WriteMeshShaderCapability(device, writer);
        WriteVariableRateShadingCapability(device, writer, cancellationToken);
        WriteWorkGraphCapability(device, writer);
        WriteIndirectCommandCapability(device, writer);
        writer.Write(device.TryGetCapability(out CalibratedTimestamps? _));
        WriteLinkedAdapterCapability(device, writer);
        WriteExternalResourceCapability(device, writer);
        WriteExternalTimelineCapability(device, writer);
    }

    private static void WriteSparseCapability(
        D3D12Device device,
        BinaryWriter writer,
        CancellationToken cancellationToken)
    {
        bool hasSparse = device.TryGetCapability(out SparseResources? sparse);
        writer.Write(hasSparse);
        if (hasSparse)
        {
            writer.Write(sparse!.Tier);
            writer.Write(sparse.TileSizeInBytes);
            writer.Write(sparse.BufferSupported);
            WriteFormats(writer, sparse.SupportedTexture2DFormats, cancellationToken);
            WriteFormats(writer, sparse.SupportedTexture3DFormats, cancellationToken);
            writer.Write(sparse.MaximumMappingsPerCall);
        }
    }

    private static void WriteSamplerFeedbackCapability(
        D3D12Device device,
        BinaryWriter writer,
        CancellationToken cancellationToken)
    {
        bool hasFeedback = device.TryGetCapability(out SamplerFeedback? feedback);
        writer.Write(hasFeedback);
        if (hasFeedback)
        {
            writer.Write((byte)feedback!.Tier);
            WriteFormats(writer, feedback.SupportedFormats, cancellationToken);
            writer.Write(feedback.MinimumMipRegionWidth);
            writer.Write(feedback.MinimumMipRegionHeight);
            writer.Write(feedback.FeedbackMapAlignment);
        }
    }

    private static void WriteResidencyCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasResidency = device.TryGetCapability(out Residency? residency);
        writer.Write(hasResidency);
        if (hasResidency)
        {
            writer.Write(residency!.LocalMemory);
            writer.Write(residency.NonLocalMemory);
        }
    }

    private static void WriteRayTracingCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasRayTracing = device.TryGetCapability(out RayTracing? rayTracing);
        writer.Write(hasRayTracing);
        if (hasRayTracing)
        {
            writer.Write((byte)rayTracing!.Tier);
            writer.Write(rayTracing.PipelineRayTracing);
            writer.Write(rayTracing.InlineRayQuery);
            writer.Write(rayTracing.IndirectDispatch);
            writer.Write(rayTracing.AccelerationStructureUpdate);
            writer.Write(rayTracing.Compaction);
            writer.Write(rayTracing.Serialization);
            writer.Write(rayTracing.StateObjectAdditions);
            writer.Write(rayTracing.MaximumRecursionDepth);
            writer.Write(rayTracing.MaximumPayloadSize);
            writer.Write(rayTracing.MaximumAttributeSize);
            writer.Write(rayTracing.MaximumGeometriesPerBottomLevel);
            writer.Write(rayTracing.MaximumInstancesPerTopLevel);
            writer.Write(rayTracing.MaximumPrimitivesPerBottomLevel);
            writer.Write(rayTracing.MaximumRayGenerationShaderThreads);
            writer.Write(rayTracing.MaximumShaderRecordStride);
            writer.Write(rayTracing.AccelerationStructureAlignment);
            writer.Write(rayTracing.ScratchAlignment);
            writer.Write(rayTracing.ShaderTableAlignment);
            writer.Write(rayTracing.ShaderRecordAlignment);
        }
    }

    private static void WriteMeshShaderCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasMesh = device.TryGetCapability(out MeshShaders? mesh);
        writer.Write(hasMesh);
        if (hasMesh)
        {
            writer.Write(mesh!.AmplificationShaders);
            writer.Write(mesh.IndirectDispatch);
            writer.Write(mesh.MaximumThreadGroupCountX);
            writer.Write(mesh.MaximumThreadGroupCountY);
            writer.Write(mesh.MaximumThreadGroupCountZ);
            writer.Write(mesh.MaximumTotalThreadGroupCount);
            writer.Write(mesh.MaximumThreadsPerGroup);
            writer.Write(mesh.MaximumPayloadSize);
            writer.Write(mesh.MaximumSharedMemory);
            writer.Write(mesh.MaximumOutputVertices);
            writer.Write(mesh.MaximumOutputPrimitives);
        }
    }

    private static void WriteVariableRateShadingCapability(
        D3D12Device device,
        BinaryWriter writer,
        CancellationToken cancellationToken)
    {
        bool hasVrs = device.TryGetCapability(out VariableRateShading? vrs);
        writer.Write(hasVrs);
        if (hasVrs)
        {
            writer.Write(checked((uint)vrs!.Rates.Length));
            foreach (ShadingRate rate in vrs.Rates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write((byte)rate);
            }
            writer.Write(checked((uint)vrs.Combiners.Length));
            foreach (ShadingRateCombiner combiner in vrs.Combiners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write((byte)combiner);
            }
            writer.Write(vrs.PerPrimitive);
            writer.Write(vrs.ShadingRateImage);
            writer.Write(vrs.AdditionalRates);
            writer.Write(vrs.ImageTileWidth);
            writer.Write(vrs.ImageTileHeight);
        }
    }

    private static void WriteWorkGraphCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasWorkGraphs = device.TryGetCapability(out WorkGraphs? workGraphs);
        writer.Write(hasWorkGraphs);
        if (hasWorkGraphs)
        {
            writer.Write((byte)workGraphs!.Tier);
            writer.Write(workGraphs.CpuInput);
            writer.Write(workGraphs.GpuInput);
            writer.Write(workGraphs.MaximumNodeCount);
            writer.Write(workGraphs.MaximumInputRecordSize);
            writer.Write(workGraphs.MaximumOutputRecordSize);
            writer.Write(workGraphs.MaximumDispatchGridDimension);
            writer.Write(workGraphs.MaximumDispatchGridVolume);
            writer.Write(workGraphs.MaximumOneDimensionalDispatchGridX);
        }
    }

    private static void WriteIndirectCommandCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasIndirect = device.TryGetCapability(out IndirectCommands? indirect);
        writer.Write(hasIndirect);
        if (hasIndirect)
        {
            IndirectCommands capability = indirect!;
            writer.Write(GetIndirectArgumentMask(capability));
            writer.Write(capability.ArgumentBufferAlignment);
            writer.Write(capability.CountBufferAlignment);
            writer.Write(capability.MaximumCommandCount);
            writer.Write(capability.MaximumStride);
        }
    }

    private static void WriteLinkedAdapterCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasLinkedAdapters = device.TryGetCapability(out LinkedAdapters? linkedAdapters);
        writer.Write(hasLinkedAdapters);
        if (hasLinkedAdapters)
        {
            writer.Write(linkedAdapters!.NodeCount);
            writer.Write(linkedAdapters.ResourceCreationMask);
            writer.Write(linkedAdapters.ResourceVisibilityMask);
            writer.Write(linkedAdapters.QueueMask);
            writer.Write(linkedAdapters.PipelineMask);
        }
    }

    private static void WriteExternalResourceCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasExternalResources = device.TryGetCapability(out ExternalResources? externalResources);
        writer.Write(hasExternalResources);
        if (hasExternalResources)
        {
            ExternalResources capability = externalResources!;
            writer.Write(GetExternalHandleMask(
                capability.SupportsBufferImport(ExternalHandleType.OpaqueWin32),
                capability.SupportsBufferImport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsBufferExport(ExternalHandleType.OpaqueWin32),
                capability.SupportsBufferExport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsTextureImport(ExternalHandleType.OpaqueWin32),
                capability.SupportsTextureImport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsTextureExport(ExternalHandleType.OpaqueWin32),
                capability.SupportsTextureExport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsHeapImport(ExternalHandleType.OpaqueWin32),
                capability.SupportsHeapImport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsHeapExport(ExternalHandleType.OpaqueWin32),
                capability.SupportsHeapExport(ExternalHandleType.OpaqueWin32Kmt)));
        }
    }

    private static void WriteExternalTimelineCapability(D3D12Device device, BinaryWriter writer)
    {
        bool hasExternalTimelines = device.TryGetCapability(out ExternalTimelines? externalTimelines);
        writer.Write(hasExternalTimelines);
        if (hasExternalTimelines)
        {
            ExternalTimelines capability = externalTimelines!;
            writer.Write(GetExternalHandleMask(
                capability.SupportsImport(ExternalHandleType.OpaqueWin32),
                capability.SupportsImport(ExternalHandleType.OpaqueWin32Kmt)));
            writer.Write(GetExternalHandleMask(
                capability.SupportsExport(ExternalHandleType.OpaqueWin32),
                capability.SupportsExport(ExternalHandleType.OpaqueWin32Kmt)));
        }
    }

    private static ushort GetIndirectArgumentMask(IndirectCommands capability)
    {
        ushort result = 0;
        foreach (IndirectArgumentType type in Enum.GetValues<IndirectArgumentType>())
        {
            if (capability.Supports(type))
                result |= checked((ushort)(1 << (int)type));
        }
        return result;
    }

    private static byte GetExternalHandleMask(bool opaqueWin32, bool opaqueWin32Kmt) =>
        checked((byte)((opaqueWin32 ? 1 : 0) | (opaqueWin32Kmt ? 2 : 0)));

    private static void WriteDeviceLimits(BinaryWriter writer, in DeviceLimits limits)
    {
        writer.Write(limits.MaximumBufferSize);
        writer.Write(limits.MaximumTextureDimension1D);
        writer.Write(limits.MaximumTextureDimension2D);
        writer.Write(limits.MaximumTextureDimension3D);
        writer.Write(limits.MaximumTextureArrayLayers);
        writer.Write(limits.MaximumColorAttachments);
        writer.Write(limits.MaximumViewports);
        writer.Write(limits.ResourceDescriptorCapacity);
        writer.Write(limits.SamplerDescriptorCapacity);
        writer.Write(limits.ConstantBufferAlignment);
        writer.Write(limits.TextureDataPitchAlignment);
        writer.Write(limits.TextureDataPlacementAlignment);
    }

    private static void WriteFormatSupport(
        BinaryWriter writer,
        ReadOnlySpan<FormatSupport> formats,
        CancellationToken cancellationToken)
    {
        writer.Write(checked((uint)formats.Length));
        foreach (ref readonly FormatSupport support in formats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write((ushort)support.Format);
            writer.Write((uint)support.Features);
            writer.Write((byte)support.SupportedSampleCounts);
            writer.Write((byte)support.SupportedSparseSampleCounts);
        }
    }

    private static void WriteFormats(
        BinaryWriter writer,
        ReadOnlySpan<Format> formats,
        CancellationToken cancellationToken)
    {
        writer.Write(checked((uint)formats.Length));
        foreach (Format format in formats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write((ushort)format);
        }
    }

    private static void WriteCanonicalString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        writer.Write(checked((uint)byteCount));
        if (byteCount == 0)
            return;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes);
    }

    private static void WriteCanonicalBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(checked((uint)value.Length));
        writer.Write(value);
    }

    internal static uint CanonicalizePipelineKeySingle(float value)
    {
        if (value == 0f)
            return 0;
        return float.IsNaN(value)
            ? 0x7FC0_0000u
            : BitConverter.SingleToUInt32Bits(value);
    }

    private static void WriteCanonicalSingle(BinaryWriter writer, float value) =>
        writer.Write(CanonicalizePipelineKeySingle(value));

    private static byte[] CreateCanonicalPipelineKey(
        D3D12Device device,
        byte family,
        Action<BinaryWriter> writeProgramIdentities,
        Action<BinaryWriter> writeRootSignatures,
        Action<BinaryWriter> writeFamily)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PipelineKeySchemaVersion);
            writeProgramIdentities(writer);

            // Slang's GetEntryPointHash is the authority for the selected target,
            // profile, capabilities and compiler options. Its stable digest is
            // written by writeProgramIdentities; these fields identify how that
            // digest and the emitted bytes are interpreted by this backend.
            writer.Write(SlangTargetSettingsIdentityVersion);
            writer.Write((int)SlangCompileTarget.Dxil);
            WriteCanonicalString(writer, SlangToolchainIdentity.Version);

            writer.Write(RootSignatureSchemaVersion);
            writeRootSignatures(writer);
            writer.Write(family);
            writer.Write(device.EnabledNodeMask);
            writeFamily(writer);
        }

        return SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static partial class RequireD3D12
    {
        internal static D3D12PipelineCache PipelineCache(PipelineCache value) =>
            value as D3D12PipelineCache ??
            throw new ArgumentException(
                "The PipelineCache was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
