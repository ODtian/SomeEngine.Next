using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Assets.Schema.Texture;

namespace SomeEngine.Render;

/// <summary>
/// Optional per-material descriptor-value storage. Material slots remain material-local;
/// this extension allocates a variable-length value span for each material it uses.
/// </summary>
public sealed class MaterialBindless : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly EngineTexture _missingTexture;
    private readonly uint[] _mirror;
    private readonly SomeEngine.Graphics.Buffer _values;
    private readonly RangeAllocator _valueRanges;
    private readonly uint _descriptorSlotsPerShape;
    private readonly TextureDeviceRealizations _textures;
    private readonly Dictionary<Material, MaterialState> _materials =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DescriptorSlotDesc, DescriptorPool> _descriptorPools = [];
    private readonly List<PendingRemoval> _pendingRemovals = [];
    private readonly List<ValueRange> _dirty = [];
    private readonly HashSet<GraphPassId> _frameConsumers = [];
    private readonly HashSet<GraphPassId> _frameBoundPasses = [];
    private readonly List<GraphPassId> _frameUploads = [];
    private ulong _frameIdentity;
    private GraphBufferId _frameValues;
    private GraphBufferSrvId _frameValuesView;
    private bool _valuesInitialized;
    private bool _disposed;

    public MaterialBindless(
        IGraphicsBackend backend,
        Device device,
        EngineTexture missingTexture,
        uint maximumValueSlots,
        uint descriptorSlotsPerShape = 1024)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _missingTexture = missingTexture ?? throw new ArgumentNullException(nameof(missingTexture));
        if (maximumValueSlots == 0)
            throw new ArgumentOutOfRangeException(nameof(maximumValueSlots));
        if (descriptorSlotsPerShape == 0)
            throw new ArgumentOutOfRangeException(nameof(descriptorSlotsPerShape));

        _descriptorSlotsPerShape = descriptorSlotsPerShape;
        _mirror = new uint[checked((int)maximumValueSlots)];
        _valueRanges = new RangeAllocator(maximumValueSlots);
        _values = _backend.CreateBuffer(
            _device,
            new BufferDesc(
                checked((ulong)maximumValueSlots * sizeof(uint)),
                BufferUsages.CopyDestination | BufferUsages.ShaderRead,
                "MaterialBindlessValues"));
        _textures = new TextureDeviceRealizations(_backend, _device);
        AddDirty(0, maximumValueSlots);
    }

    public SomeEngine.Graphics.Buffer ValuesBuffer => _values;

    public uint Use(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        Material material,
        PipelineSync sync)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        CollectCompleted();
        EnsureFrame(ref frame);

        GraphPassId consumer = pass.Id;
        if (_frameConsumers.Add(consumer))
            foreach (GraphPassId upload in _frameUploads)
                frame.OrderAfter(consumer, upload);

        if (!_materials.TryGetValue(material, out MaterialState? state))
        {
            uint count = checked((uint)material.SlotCount);
            uint baseSlot = _valueRanges.Allocate(count);
            state = new MaterialState(baseSlot, count);
            _materials.Add(material, state);
            if (count != 0)
            {
                _mirror.AsSpan(checked((int)baseSlot), checked((int)count)).Clear();
                AddDirty(baseSlot, count);
            }
        }

        UpdateMaterial(ref frame, ref pass, material, state, sync);
        FlushDirty(ref frame);

        if (_frameBoundPasses.Add(consumer))
            _ = pass.Bind(_frameValuesView, sync);
        return state.Base;
    }

    public void Upload(ref RenderGraphFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CollectCompleted();
        EnsureFrame(ref frame);
        FlushDirty(ref frame);
    }

    public void Remove(Material material, QueueCompletion completion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        if (!ReferenceEquals(completion.Queue.Device, _device))
            throw new ArgumentException(
                "The completion must belong to the MaterialBindless Device.",
                nameof(completion));
        CollectCompleted();
        if (_materials.Remove(material, out MaterialState? state))
            _pendingRemovals.Add(new PendingRemoval(state, completion));
    }

    private void UpdateMaterial(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        Material material,
        MaterialState state,
        PipelineSync sync)
    {
        IReadOnlyList<MaterialSlotDefinition> definitions = material.Slots;
        for (uint slot = 0; slot < state.Count; slot++)
        {
            MaterialSlotDefinition definition = definitions[checked((int)slot)];
            if (typeof(EngineTexture).IsAssignableFrom(definition.ValueType))
            {
                object? raw = material.GetSlotValue(slot);
                EngineTexture texture = raw switch
                {
                    null => _missingTexture,
                    EngineTexture value => value,
                    _ => throw new InvalidOperationException(
                        $"Material slot {slot} declares Texture but contains {raw.GetType().FullName}."),
                };
                TextureUse use = _textures.Use(ref frame, ref pass, texture, sync);
                UpdateTextureSlot(ref frame, material, state, slot, texture, use);
            }
            else if (definition.ValueType == typeof(SamplerDesc))
            {
                object? raw = material.GetSlotValue(slot);
                if (raw is not SamplerDesc sampler)
                    throw new InvalidOperationException(
                        $"Material slot {slot} declares SamplerDesc but has no sampler value.");
                UpdateSamplerSlot(ref frame, material, state, slot, sampler);
            }
            // Ordinary scalar/vector slots intentionally remain zero in this descriptor-value extension.
        }
    }

    private void UpdateTextureSlot(
        ref RenderGraphFrame frame,
        Material material,
        MaterialState state,
        uint slot,
        EngineTexture texture,
        in TextureUse use)
    {
        SlotState? current = state.Slots[checked((int)slot)];
        DescriptorAllocation allocation;
        if (current?.Allocation is not { } existing || existing.Pool.Shape != use.Descriptor)
        {
            allocation = AllocateDescriptor(use.Descriptor);
            if (current?.Allocation is { } retired)
                frame.RetireAfterSubmittedFrames(
                    new DisposeAction(() => retired.Pool.Free(retired.Slot)));
        }
        else
        {
            allocation = existing;
        }

        ulong slotRevision = material.GetSlotRevision(slot);
        if (current is null
            || !ReferenceEquals(current.Texture, texture)
            || current.TextureRevision != texture.Revision
            || !ReferenceEquals(current.TextureView, use.View)
            || current.SlotRevision != slotRevision
            || current.Allocation != allocation)
        {
            _backend.WriteDescriptor(
                allocation.Pool.Table,
                allocation.Slot,
                ResourceBinding.SampledTexture(use.View));
            WriteValue(checked(state.Base + slot), allocation.Index.Value);
            state.Slots[checked((int)slot)] = new SlotState
            {
                Allocation = allocation,
                SlotRevision = slotRevision,
                Texture = texture,
                TextureRevision = texture.Revision,
                TextureView = use.View,
            };
        }
    }

    private void UpdateSamplerSlot(
        ref RenderGraphFrame frame,
        Material material,
        MaterialState state,
        uint slot,
        in SamplerDesc description)
    {
        SlotState? current = state.Slots[checked((int)slot)];
        ulong slotRevision = material.GetSlotRevision(slot);
        if (current is not null
            && current.SlotRevision == slotRevision
            && current.SamplerDescription == description)
        {
            return;
        }

        DescriptorSlotDesc shape = new(ResourceBindingType.Sampler);
        DescriptorAllocation allocation = current?.Allocation is { } existing
            ? existing
            : AllocateDescriptor(shape);
        Sampler sampler = _backend.CreateSampler(_device, description);
        try
        {
            _backend.WriteDescriptor(
                allocation.Pool.Table,
                allocation.Slot,
                ResourceBinding.SampledWith(sampler));
        }
        catch
        {
            sampler.Dispose();
            if (current?.Allocation is null)
                allocation.Pool.Free(allocation.Slot);
            throw;
        }

        if (current?.Sampler is { } retired)
            frame.RetireAfterSubmittedFrames(retired);
        WriteValue(checked(state.Base + slot), allocation.Index.Value);
        state.Slots[checked((int)slot)] = new SlotState
        {
            Allocation = allocation,
            SlotRevision = slotRevision,
            Sampler = sampler,
            SamplerDescription = description,
        };
    }

    private DescriptorAllocation AllocateDescriptor(in DescriptorSlotDesc shape)
    {
        if (!_descriptorPools.TryGetValue(shape, out DescriptorPool? pool))
        {
            pool = new DescriptorPool(
                _backend,
                _device,
                shape,
                _descriptorSlotsPerShape);
            _descriptorPools.Add(shape, pool);
        }
        return pool.Allocate();
    }

    private void EnsureFrame(ref RenderGraphFrame frame)
    {
        if (_frameIdentity == frame.FrameIdentity)
            return;
        _frameIdentity = frame.FrameIdentity;
        _frameConsumers.Clear();
        _frameBoundPasses.Clear();
        _frameUploads.Clear();

        BufferBoundaryState boundary = _valuesInitialized
            ? new BufferBoundaryState(
                BufferRange.Whole,
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                ResourceContentState.Defined)
            : new BufferBoundaryState(
                BufferRange.Whole,
                _values.InitialSync,
                _values.InitialAccess,
                ResourceContentState.Undefined);
        _frameValues = frame.Import(_values, [boundary]);
        _frameValuesView = frame.CreateBufferSrv(
            _frameValues,
            BufferRange.Whole,
            structureStride: sizeof(uint),
            label: "MaterialBindlessValues SRV");
    }

    private void FlushDirty(ref RenderGraphFrame frame)
    {
        if (_dirty.Count == 0)
            return;
        foreach (ValueRange dirty in _dirty)
        {
            ReadOnlySpan<uint> values = _mirror.AsSpan(
                checked((int)dirty.Start),
                checked((int)dirty.Count));
            GraphBufferId upload = frame.Upload(
                values,
                BufferUsages.CopySource,
                "MaterialBindlessValues dirty upload");
            var state = new ValuesUploadPass(
                upload,
                _frameValues,
                checked((ulong)dirty.Start * sizeof(uint)),
                checked((ulong)dirty.Count * sizeof(uint)));
            GraphPassId copy = frame.AddCopyPass(
                "Upload MaterialBindlessValues",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition definition, ref ValuesUploadPass value) =>
                {
                    _ = definition.Read(
                        value.Upload,
                        BufferRange.Whole,
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = definition.Write(
                        value.Destination,
                        new BufferRange(value.DestinationOffset, value.Size),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Defined);
                },
                static (ref CopyPassCommandScope commands, in ValuesUploadPass value) =>
                {
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(value.Upload),
                        0,
                        commands.GetBuffer(value.Destination),
                        value.DestinationOffset,
                        value.Size));
                });
            _frameUploads.Add(copy);
            foreach (GraphPassId consumer in _frameConsumers)
                frame.OrderAfter(consumer, copy);
        }
        _dirty.Clear();
        _valuesInitialized = true;
    }

    private void WriteValue(uint slot, uint value)
    {
        int index = checked((int)slot);
        if (_mirror[index] == value)
            return;
        _mirror[index] = value;
        AddDirty(slot, 1);
    }

    private void AddDirty(uint start, uint count)
    {
        if (count == 0)
            return;
        uint end = checked(start + count);
        int index = 0;
        while (index < _dirty.Count && checked(_dirty[index].Start + _dirty[index].Count) < start)
            index++;
        while (index < _dirty.Count && _dirty[index].Start <= end)
        {
            ValueRange range = _dirty[index];
            start = Math.Min(start, range.Start);
            end = Math.Max(end, checked(range.Start + range.Count));
            _dirty.RemoveAt(index);
        }
        _dirty.Insert(index, new ValueRange(start, end - start));
    }

    private void CollectCompleted()
    {
        for (int index = _pendingRemovals.Count - 1; index >= 0; index--)
        {
            PendingRemoval pending = _pendingRemovals[index];
            if (!_backend.IsComplete(pending.Completion))
                continue;
            Release(pending.State);
            _pendingRemovals.RemoveAt(index);
        }
    }

    private void Release(MaterialState state)
    {
        _valueRanges.Free(state.Base, state.Count);
        foreach (SlotState? slot in state.Slots)
        {
            if (slot?.Allocation is { } allocation)
                allocation.Pool.Free(allocation.Slot);
            slot?.Sampler?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (MaterialState state in _materials.Values)
            foreach (SlotState? slot in state.Slots)
                slot?.Sampler?.Dispose();
        foreach (PendingRemoval pending in _pendingRemovals)
            foreach (SlotState? slot in pending.State.Slots)
                slot?.Sampler?.Dispose();
        _materials.Clear();
        _pendingRemovals.Clear();
        foreach (DescriptorPool pool in _descriptorPools.Values)
            pool.Dispose();
        _descriptorPools.Clear();
        _textures.Dispose();
        _values.Dispose();
    }

    private sealed class MaterialState
    {
        internal MaterialState(uint baseSlot, uint count)
        {
            Base = baseSlot;
            Count = count;
            Slots = new SlotState?[checked((int)count)];
        }

        internal uint Base { get; }
        internal uint Count { get; }
        internal SlotState?[] Slots { get; }
    }

    private sealed class SlotState
    {
        internal DescriptorAllocation? Allocation { get; init; }
        internal ulong SlotRevision { get; init; }
        internal EngineTexture? Texture { get; init; }
        internal ulong TextureRevision { get; init; }
        internal TextureSrv? TextureView { get; init; }
        internal Sampler? Sampler { get; init; }
        internal SamplerDesc SamplerDescription { get; init; }
    }

    private sealed class DescriptorPool : IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly Stack<uint> _free = [];

        internal DescriptorPool(
            IGraphicsBackend backend,
            Device device,
            in DescriptorSlotDesc shape,
            uint capacity)
        {
            _backend = backend;
            Shape = shape;
            DescriptorSlotDesc[] slots = new DescriptorSlotDesc[checked((int)capacity)];
            slots.AsSpan().Fill(shape);
            Table = backend.CreateDescriptorTable(
                device,
                slots,
                $"MaterialBindless {shape.Type} descriptors");
            for (uint slot = capacity; slot > 0; slot--)
                _free.Push(slot - 1);
        }

        internal DescriptorSlotDesc Shape { get; }
        internal DescriptorTable Table { get; }

        internal DescriptorAllocation Allocate()
        {
            if (!_free.TryPop(out uint slot))
                throw new InvalidOperationException(
                    $"MaterialBindless has exhausted the configured {Shape.Type} descriptor shape pool.");
            return new DescriptorAllocation(
                this,
                slot,
                _backend.GetDescriptorIndex(Table, slot));
        }

        internal void Free(uint slot) => _free.Push(slot);

        public void Dispose() => Table.Dispose();
    }

    private readonly record struct DescriptorAllocation(
        DescriptorPool Pool,
        uint Slot,
        DescriptorIndex Index);

    private readonly record struct PendingRemoval(
        MaterialState State,
        QueueCompletion Completion);

    private readonly record struct ValueRange(uint Start, uint Count);

    private readonly record struct ValuesUploadPass(
        GraphBufferId Upload,
        GraphBufferId Destination,
        ulong DestinationOffset,
        ulong Size);
}
