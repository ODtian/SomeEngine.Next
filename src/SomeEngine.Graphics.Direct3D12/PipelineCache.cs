using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const uint PipelineCacheEnvelopeSchemaVersion = PipelineCacheEnvelope.SchemaVersion;
    private const int PipelineCacheHardEntryCountLimit = PipelineCacheEnvelope.HardEntryCountLimit;
    private const int PipelineCacheEmptyEnvelopeByteCount = PipelineCacheEnvelope.EmptyEnvelopeByteCount;
    private const int PipelineCacheEntryFixedByteCount = PipelineCacheEnvelope.EntryFixedByteCount;
    private const int PipelineCacheHashByteCount = PipelineCacheEnvelope.HashByteCount;
    private const int PipelineCacheCancellationChunkByteCount = 64 * 1024;
    // Stable little-endian wire identity: ASCII "D3D12" followed by three zero bytes.
    private const ulong D3D12PipelineCacheBackendTag = 0x0000_0032_3144_3344UL;
    private const uint PipelineKeySchemaVersion = 2;
    private const uint D3D12BackendAbiVersion = 1;
    private const byte SlangTargetSettingsIdentityVersion = 1;
    private const string AgilityPackageVersion = "1.619.5";

    public PipelineCache CreatePipelineCache(
        Device device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken = default)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
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
                var entries = new PipelineCacheEntry[_entries.Count];
                int index = 0;
                foreach ((CacheEntryKey key, CacheEntry entry) in _entries)
                {
                    entries[index++] = new PipelineCacheEntry(
                        key.Backend,
                        key.Family,
                        key.Key,
                        key.Compatibility,
                        entry.Payload);
                }
                return PipelineCacheEnvelope.Serialize(
                    entries,
                    Limits,
                    cancellationToken);
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
            ParsedPipelineCache parsed = PipelineCacheEnvelope.Parse(
                data,
                Limits,
                cancellationToken);
            var candidate = new SortedDictionary<CacheEntryKey, CacheEntry>();
            int decodedByteCount = 0;
            foreach (PipelineCacheEntry entry in parsed.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidate.Add(
                    new CacheEntryKey(
                        entry.Backend,
                        entry.Family,
                        entry.Key,
                        entry.Compatibility),
                    new CacheEntry(
                        entry.Payload,
                        PipelineCacheEnvelope.GetEntryWireByteCount(entry.Payload.Length)));
                decodedByteCount = checked(decodedByteCount + entry.Payload.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _entries = candidate;
            _residentWireByteCount = data.Length;
            _residentDecodedByteCount = decodedByteCount;
        }

        private PipelineCacheLimits Limits => new(
            _maximumEntryCount,
            _maximumByteCount,
            _maximumDecodedByteCount);

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

        private static void ComputeSha256(ReadOnlySpan<byte> source, Span<byte> destination,
            CancellationToken cancellationToken)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (!source.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(source.Length, PipelineCacheCancellationChunkByteCount);
                hash.AppendData(source[..count]);
                source = source[count..];
            }
            cancellationToken.ThrowIfCancellationRequested();
            hash.GetHashAndReset(destination);
        }

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
            writer.Write(rayTracing.ShaderRecordResourceBindings);
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
        writer.Write(limits.MaximumConstantBufferRange);
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
