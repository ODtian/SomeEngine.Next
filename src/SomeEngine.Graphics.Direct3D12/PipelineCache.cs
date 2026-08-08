using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const uint PipelineCacheEnvelopeSchemaVersion = 2;
    private const uint PipelineKeySchemaVersion = 2;
    private const uint D3D12BackendAbiVersion = 1;
    private const uint StateObjectReplaySchemaVersion = 1;
    private const byte SlangTargetSettingsIdentityVersion = 1;
    private const string AgilityPackageVersion = "1.619.5";
    private const string SlangCompilerVersion = "2026.4.2";

    public PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache result = new(nativeDevice, desc.Data, desc.Label);
        nativeDevice.RegisterChild(result);
        return result;
    }

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount)
    {
        byte[] data = NativeCast.PipelineCache(cache).Serialize();
        requiredByteCount = data.Length;
        if (destination.Length < data.Length)
            return false;
        data.CopyTo(destination);
        return true;
    }

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources)
    {
        D3D12PipelineCache nativeDestination = NativeCast.PipelineCache(destination);
        foreach (PipelineCache source in sources)
            nativeDestination.Merge(NativeCast.PipelineCache(source));
    }

    private sealed class D3D12PipelineCache : PipelineCache
    {
        private static readonly byte[] Magic = "SERHIC01"u8.ToArray();

        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private readonly SortedDictionary<CacheEntryKey, byte[]> _entries = [];
        private int _released;

        internal D3D12PipelineCache(
            D3D12Device device,
            ReadOnlySpan<byte> data,
            string? label)
            : base(device, label)
        {
            _device = device;
            Compatibility = ComputeCompatibility(device);
            if (!data.IsEmpty)
                Parse(data);
        }

        internal byte[] Compatibility { get; }

        internal bool TryGet(byte family, ReadOnlySpan<byte> key, out byte[] data)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_entries.TryGetValue(
                    new CacheEntryKey(family, Convert.ToHexString(key), Convert.ToHexString(Compatibility)),
                    out byte[]? result))
                {
                    data = result;
                    return true;
                }
                data = [];
                return false;
            }
        }

        internal void Store(byte family, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                CacheEntryKey entry = new(
                    family,
                    Convert.ToHexString(key),
                    Convert.ToHexString(Compatibility));
                byte[] candidate = data.ToArray();
                if (_entries.TryGetValue(entry, out byte[]? existing))
                {
                    if (CompareBytes(candidate, existing) < 0)
                        _entries[entry] = candidate;
                }
                else
                {
                    _entries.Add(entry, candidate);
                }
            }
        }

        internal void Merge(D3D12PipelineCache source)
        {
            KeyValuePair<CacheEntryKey, byte[]>[] snapshot = source.Snapshot();
            lock (_gate)
            {
                ThrowIfDisposed();
                foreach ((CacheEntryKey key, byte[] value) in snapshot)
                {
                    if (_entries.TryGetValue(key, out byte[]? existing))
                    {
                        if (CompareBytes(value, existing) < 0)
                            _entries[key] = value;
                    }
                    else
                    {
                        _entries.Add(key, value);
                    }
                }
            }
        }

        internal byte[] Serialize()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                using MemoryStream stream = new();
                using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(PipelineCacheEnvelopeSchemaVersion);
                    writer.Write(_entries.Count);
                    foreach ((CacheEntryKey key, byte[] payload) in _entries)
                    {
                        writer.Write(key.Family);
                        writer.Write(Convert.FromHexString(key.Key));
                        writer.Write(Convert.FromHexString(key.Compatibility));
                        writer.Write(payload.Length);
                        writer.Write(payload);
                        writer.Write(SHA256.HashData(payload));
                    }
                }
                byte[] body = stream.ToArray();
                byte[] result = new byte[checked(body.Length + 32)];
                body.CopyTo(result, 0);
                SHA256.HashData(body).CopyTo(result, body.Length);
                return result;
            }
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_gate)
                _entries.Clear();
            _device.UnregisterChild(this);
        }

        private void Parse(ReadOnlySpan<byte> data)
        {
            try
            {
                if (data.Length < Magic.Length + 4 + 4 + 32)
                    throw new InvalidDataException("The pipeline-cache envelope is truncated.");
                ReadOnlySpan<byte> body = data[..^32];
                if (!SHA256.HashData(body).AsSpan().SequenceEqual(data[^32..]))
                    throw new InvalidDataException("The pipeline-cache envelope checksum is invalid.");
                int offset = 0;
                if (!body[..Magic.Length].SequenceEqual(Magic))
                    throw new InvalidDataException("The pipeline-cache magic is invalid.");
                offset += Magic.Length;
                uint version = ReadUInt32(body, ref offset);
                if (version != PipelineCacheEnvelopeSchemaVersion)
                    throw new InvalidDataException("The pipeline-cache schema is unsupported.");
                uint count = ReadUInt32(body, ref offset);
                if (count > 1_000_000)
                    throw new InvalidDataException("The pipeline-cache entry count is invalid.");
                for (uint index = 0; index < count; index++)
                {
                    byte family = ReadByte(body, ref offset);
                    byte[] key = ReadBytes(body, ref offset, 32);
                    byte[] compatibility = ReadBytes(body, ref offset, 32);
                    uint length = ReadUInt32(body, ref offset);
                    byte[] payload = ReadBytes(body, ref offset, checked((int)length));
                    byte[] checksum = ReadBytes(body, ref offset, 32);
                    if (!SHA256.HashData(payload).AsSpan().SequenceEqual(checksum))
                        throw new InvalidDataException("A pipeline-cache section checksum is invalid.");
                    CacheEntryKey entry = new(
                        family,
                        Convert.ToHexString(key),
                        Convert.ToHexString(compatibility));
                    if (!_entries.TryAdd(entry, payload))
                        throw new InvalidDataException("The pipeline-cache envelope contains a duplicate section.");
                }
                if (offset != body.Length)
                    throw new InvalidDataException("The pipeline-cache envelope has trailing bytes.");
            }
            catch (Exception exception) when (exception is not GraphicsException)
            {
                _entries.Clear();
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "The pipeline-cache envelope is corrupt.",
                    innerException: exception);
            }
        }

        private KeyValuePair<CacheEntryKey, byte[]>[] Snapshot()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _entries
                    .Select(static pair =>
                        new KeyValuePair<CacheEntryKey, byte[]>(pair.Key, (byte[])pair.Value.Clone()))
                    .ToArray();
            }
        }

        private static byte[] ComputeCompatibility(D3D12Device device)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(PipelineCacheEnvelopeSchemaVersion);
                writer.Write(PipelineKeySchemaVersion);
                writer.Write(D3D12BackendAbiVersion);
                writer.Write(RootLayoutSchemaVersion);
                writer.Write(StateObjectReplaySchemaVersion);
                writer.Write(AgilitySdkVersion);
                WriteCanonicalString(writer, AgilityPackageVersion);
                WriteCanonicalString(writer, SlangCompilerVersion);

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
                writer.Write((ulong)capabilities.Features);
                WriteDeviceLimits(writer, capabilities.Limits);
                writer.Write(capabilities.SupportsBundles);
                writer.Write(capabilities.SupportsPipelineStatistics);
                writer.Write(capabilities.SupportsStreamOutputStatistics);
                WriteFormatSupport(writer, capabilities.Formats);
                writer.Write(device.CapabilitiesSnapshot.NodeCount);
                writer.Write(device.EnhancedBarriers);

                WriteCapabilitySnapshot(device, writer);
            }

            return SHA256.HashData(
                stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }

        private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
            left.SequenceCompareTo(right);

        private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            if (source.Length - offset < 4)
                throw new EndOfStreamException();
            uint result = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            offset += 4;
            return result;
        }

        private static byte ReadByte(ReadOnlySpan<byte> source, ref int offset)
        {
            if ((uint)offset >= (uint)source.Length)
                throw new EndOfStreamException();
            return source[offset++];
        }

        private static byte[] ReadBytes(ReadOnlySpan<byte> source, ref int offset, int length)
        {
            if (length < 0 || source.Length - offset < length)
                throw new EndOfStreamException();
            byte[] result = source.Slice(offset, length).ToArray();
            offset += length;
            return result;
        }

        private readonly record struct CacheEntryKey(
            byte Family,
            string Key,
            string Compatibility) : IComparable<CacheEntryKey>
        {
            public int CompareTo(CacheEntryKey other)
            {
                int result = Family.CompareTo(other.Family);
                if (result != 0)
                    return result;
                result = StringComparer.Ordinal.Compare(Key, other.Key);
                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(Compatibility, other.Compatibility);
            }
        }
    }

    private static void WriteCapabilitySnapshot(D3D12Device device, BinaryWriter writer)
    {
        bool hasSparse = device.TryGetCapability(out SparseResources? sparse);
        writer.Write(hasSparse);
        if (hasSparse)
        {
            writer.Write(sparse!.Tier);
            writer.Write(sparse.TileSizeInBytes);
            writer.Write(sparse.BufferSupported);
            WriteFormats(writer, sparse.SupportedTexture2DFormats);
            WriteFormats(writer, sparse.SupportedTexture3DFormats);
            writer.Write(sparse.MaximumMappingsPerCall);
        }

        bool hasFeedback = device.TryGetCapability(out SamplerFeedback? feedback);
        writer.Write(hasFeedback);
        if (hasFeedback)
        {
            writer.Write((byte)feedback!.Tier);
            WriteFormats(writer, feedback.SupportedFormats);
            writer.Write(feedback.MinimumMipRegionWidth);
            writer.Write(feedback.MinimumMipRegionHeight);
            writer.Write(feedback.FeedbackMapAlignment);
        }

        bool hasResidency = device.TryGetCapability(out Residency? residency);
        writer.Write(hasResidency);
        if (hasResidency)
        {
            writer.Write(residency!.LocalMemory);
            writer.Write(residency.NonLocalMemory);
        }

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

        bool hasVrs = device.TryGetCapability(out VariableRateShading? vrs);
        writer.Write(hasVrs);
        if (hasVrs)
        {
            writer.Write(checked((uint)vrs!.Rates.Length));
            foreach (ShadingRate rate in vrs.Rates)
                writer.Write((byte)rate);
            writer.Write(checked((uint)vrs.Combiners.Length));
            foreach (ShadingRateCombiner combiner in vrs.Combiners)
                writer.Write((byte)combiner);
            writer.Write(vrs.PerPrimitive);
            writer.Write(vrs.ShadingRateImage);
            writer.Write(vrs.AdditionalRates);
            writer.Write(vrs.ImageTileWidth);
            writer.Write(vrs.ImageTileHeight);
        }

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
            writer.Write(workGraphs.MaximumInputRecordCount);
        }

        bool hasIndirect = device.TryGetCapability(out IndirectCommands? indirect);
        writer.Write(hasIndirect);
        if (hasIndirect)
        {
            writer.Write((ushort)indirect!.ArgumentTypes);
            writer.Write(indirect.ArgumentBufferAlignment);
            writer.Write(indirect.CountBufferAlignment);
            writer.Write(indirect.MaximumCommandCount);
            writer.Write(indirect.MaximumStride);
        }

        writer.Write(device.TryGetCapability(out CalibratedTimestamps? _));

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

        bool hasExternalResources = device.TryGetCapability(out ExternalResources? externalResources);
        writer.Write(hasExternalResources);
        if (hasExternalResources)
        {
            writer.Write((byte)externalResources!.BufferImportHandleTypes);
            writer.Write((byte)externalResources.BufferExportHandleTypes);
            writer.Write((byte)externalResources.TextureImportHandleTypes);
            writer.Write((byte)externalResources.TextureExportHandleTypes);
            writer.Write((byte)externalResources.HeapImportHandleTypes);
            writer.Write((byte)externalResources.HeapExportHandleTypes);
        }

        bool hasExternalTimelines = device.TryGetCapability(out ExternalTimelines? externalTimelines);
        writer.Write(hasExternalTimelines);
        if (hasExternalTimelines)
        {
            writer.Write((byte)externalTimelines!.ImportHandleTypes);
            writer.Write((byte)externalTimelines.ExportHandleTypes);
        }
    }

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
        ReadOnlySpan<FormatSupport> formats)
    {
        writer.Write(checked((uint)formats.Length));
        foreach (ref readonly FormatSupport support in formats)
        {
            writer.Write((ushort)support.Format);
            writer.Write((uint)support.Features);
            writer.Write((byte)support.SupportedSampleCounts);
            writer.Write((byte)support.SupportedSparseSampleCounts);
        }
    }

    private static void WriteFormats(BinaryWriter writer, ReadOnlySpan<Format> formats)
    {
        writer.Write(checked((uint)formats.Length));
        foreach (Format format in formats)
            writer.Write((ushort)format);
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
        Action<BinaryWriter> writeRootLayouts,
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
            WriteCanonicalString(writer, SlangCompilerVersion);

            writer.Write(RootLayoutSchemaVersion);
            writeRootLayouts(writer);
            writer.Write(family);
            writer.Write(device.EnabledNodeMask);
            writeFamily(writer);
        }

        return SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static partial class NativeCast
    {
        internal static D3D12PipelineCache PipelineCache(PipelineCache value)
        {
#if DEBUG
            return (D3D12PipelineCache)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<PipelineCache, D3D12PipelineCache>(ref value);
#endif
        }
    }
}
