using SlangShaderSharp;

namespace SomeEngine.Graphics.Tests;

internal sealed partial class StrictConformanceBackend
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConformanceDevice native = RequireDevice(device);
        uint resolvedNode = nodeIndex == uint.MaxValue ? 0 : nodeIndex;
        if (resolvedNode != 0 || slots.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        DescriptorTableType type = slots[0].Type == ResourceBindingType.Sampler
            ? DescriptorTableType.Sampler
            : DescriptorTableType.Resource;
        var result = new ConformanceDescriptorTable(this, native, type, slots, label);
        native.Register(result);
        return result;
    }

    public DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        ConformanceDescriptorTable native = RequireResource(table) as ConformanceDescriptorTable
            ?? throw new ArgumentException("The DescriptorTable has the wrong backend type.", nameof(table));
        if (slot >= native.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return new DescriptorIndex(native, slot);
    }

    public void WriteDescriptor(DescriptorTable table, uint slot, in ResourceBinding value)
    {
        ConformanceDescriptorTable native = RequireResource(table) as ConformanceDescriptorTable
            ?? throw new ArgumentException("The DescriptorTable has the wrong backend type.", nameof(table));
        if (slot >= native.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        DescriptorSlotDesc expected = native.GetSlotDesc(slot);
        if (value.Type != expected.Type)
            throw new ArgumentException("The descriptor value does not match the typed slot.", nameof(value));
        if (value.Value is DeviceResource resource)
        {
            _ = RequireResource(resource);
            RequireSameDevice((ConformanceDevice)native.Device, resource, nameof(value));
        }
        native.Write(slot, value);
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        Pipeline pipeline,
        in ParameterBlockBindings bindings,
        string? label = null)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformancePipeline ownerPipeline = RequireResource(pipeline) as ConformancePipeline
            ?? throw new ArgumentException("The Pipeline has the wrong backend type.", nameof(pipeline));
        RequireSameDevice(native, ownerPipeline, nameof(pipeline));
        ValidateParameterBindings(native, bindings);
        var result = new ConformancePersistentBindings(
            this,
            native,
            ownerPipeline,
            bindings.Layout,
            bindings.Resources.ToArray(),
            bindings.OrdinaryData.ToArray(),
            label);
        native.Register(result);
        return result;
    }

    public void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings)
    {
        ConformancePersistentBindings native =
            RequireResource(destination) as ConformancePersistentBindings
            ?? throw new ArgumentException(
                "The PersistentParameterBindings have the wrong backend type.",
                nameof(destination));
        if (bindings.Layout != native.Layout)
            throw new ArgumentException("The parameter layout cannot change.", nameof(bindings));
        ValidateParameterBindings((ConformanceDevice)native.Device, bindings);
        native.Replace(bindings.Resources.ToArray(), bindings.OrdinaryData.ToArray());
    }

    public void PublishDescriptors(
        Device device,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RequireDevice(device);
        if (nodeIndex is not (uint.MaxValue or 0))
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
    }

    public PipelineCache CreatePipelineCache(
        Device device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConformanceDevice native = RequireDevice(device);
        if (desc.MaximumEntryCount < 0 || desc.MaximumByteCount < 0 ||
            desc.MaximumDecodedByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc));
        }
        byte[] data = desc.Data.ToArray();
        if (desc.MaximumByteCount != 0 && data.Length > desc.MaximumByteCount)
            throw new ArgumentException("The initial cache exceeds its byte limit.", nameof(desc));
        var result = new ConformancePipelineCache(this, native, data, desc.Label);
        native.Register(result);
        return result;
    }

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConformancePipelineCache native = RequireResource(cache) as ConformancePipelineCache
            ?? throw new ArgumentException("The PipelineCache has the wrong backend type.", nameof(cache));
        byte[] data = native.GetData();
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
        ConformancePipelineCache target =
            RequireResource(destination) as ConformancePipelineCache
            ?? throw new ArgumentException("The PipelineCache has the wrong backend type.", nameof(destination));
        var parts = new byte[sources.Length][];
        int length = 0;
        for (int index = 0; index < sources.Length; index++)
        {
            ConformancePipelineCache source =
                RequireResource(sources[index]) as ConformancePipelineCache
                ?? throw new ArgumentException("A source PipelineCache has the wrong backend type.", nameof(sources));
            RequireSameDevice((ConformanceDevice)target.Device, source, nameof(sources));
            parts[index] = source.GetData();
            length = checked(length + parts[index].Length);
        }
        byte[] merged = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(merged, offset);
            offset += part.Length;
        }
        target.SetData(merged);
    }

    public Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        ConformanceDevice native = RequireDevice(device);
        if (desc.Vertex == EntryPointReflection.Null || !Enum.IsDefined(desc.Topology))
            throw new ArgumentException("The Graphics Pipeline description is incomplete.", nameof(desc));
        RequireOptionalCache(native, cache);
        return RegisterPipeline(native, PipelineType.Graphics, desc.Label);
    }

    public Task<Pipeline> CreateGraphicsPipelineAsync(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null) =>
        Task.FromResult(CreateGraphicsPipeline(device, desc, cache));

    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        ConformanceDevice native = RequireDevice(device);
        if (desc.Compute == EntryPointReflection.Null)
            throw new ArgumentException("The Compute Pipeline requires an entry point.", nameof(desc));
        RequireOptionalCache(native, cache);
        return RegisterPipeline(native, PipelineType.Compute, desc.Label);
    }

    public Task<Pipeline> CreateComputePipelineAsync(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null) =>
        Task.FromResult(CreateComputePipeline(device, desc, cache));

    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(MeshShaders));

    public Task<Pipeline> CreateMeshPipelineAsync(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null) =>
        throw Unsupported(nameof(MeshShaders));

    private Pipeline RegisterPipeline(
        ConformanceDevice device,
        PipelineType type,
        string? label)
    {
        var result = new ConformancePipeline(this, device, type, label);
        device.Register(result);
        return result;
    }

    private void RequireOptionalCache(ConformanceDevice device, PipelineCache? cache)
    {
        if (cache is null)
            return;
        ConformancePipelineCache native = RequireResource(cache) as ConformancePipelineCache
            ?? throw new ArgumentException("The PipelineCache has the wrong backend type.", nameof(cache));
        RequireSameDevice(device, native, nameof(cache));
    }

    private void ValidateParameterBindings(
        ConformanceDevice device,
        in ParameterBlockBindings bindings)
    {
        if (bindings.Layout == VariableLayoutReflection.Null)
            throw new ArgumentException("A Slang parameter layout is required.", nameof(bindings));
        foreach (ref readonly ResourceBinding binding in bindings.Resources)
        {
            if (binding.Value is not DeviceResource resource)
                continue;
            _ = RequireResource(resource);
            RequireSameDevice(device, resource, nameof(bindings));
        }
    }

    private sealed class ConformanceDescriptorTable : DescriptorTable, IConformanceObject
    {
        private readonly object _gate = new();
        private readonly ResourceBinding[] _values;

        internal ConformanceDescriptorTable(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            DescriptorTableType type,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            string? label)
            : base(device, type, 0, slots, label)
        {
            Owner = owner;
            _values = new ResourceBinding[slots.Length];
            for (int index = 0; index < slots.Length; index++)
                _values[index] = ResourceBinding.Null(slots[index].Type);
        }

        public StrictConformanceBackend Owner { get; }

        internal void Write(uint slot, in ResourceBinding value)
        {
            lock (_gate)
                _values[checked((int)slot)] = value;
        }

        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformancePersistentBindings :
        PersistentParameterBindings,
        IConformanceObject
    {
        private readonly object _gate = new();
        private ResourceBinding[] _resources;
        private byte[] _ordinaryData;

        internal ConformancePersistentBindings(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            ConformancePipeline pipeline,
            VariableLayoutReflection layout,
            ResourceBinding[] resources,
            byte[] ordinaryData,
            string? label)
            : base(device, layout, label)
        {
            Owner = owner;
            Pipeline = pipeline;
            _resources = resources;
            _ordinaryData = ordinaryData;
        }

        public StrictConformanceBackend Owner { get; }
        internal ConformancePipeline Pipeline { get; }

        internal void Replace(ResourceBinding[] resources, byte[] ordinaryData)
        {
            lock (_gate)
            {
                _resources = resources;
                _ordinaryData = ordinaryData;
            }
        }

        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformancePipeline : Pipeline, IConformanceObject
    {
        internal ConformancePipeline(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            PipelineType type,
            string? label)
            : base(device, type, label) => Owner = owner;

        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformancePipelineCache : PipelineCache, IConformanceObject
    {
        private readonly object _gate = new();
        private byte[] _data;

        internal ConformancePipelineCache(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            byte[] data,
            string? label)
            : base(device, label)
        {
            Owner = owner;
            _data = data;
        }

        public StrictConformanceBackend Owner { get; }

        internal byte[] GetData()
        {
            lock (_gate)
                return [.. _data];
        }

        internal void SetData(byte[] data)
        {
            lock (_gate)
                _data = data;
        }

        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }
}
