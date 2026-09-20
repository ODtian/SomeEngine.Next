using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render;

/// <summary>
/// Optional per-material descriptor-value storage. Material slots remain material-local;
/// this extension allocates a variable-length value span for each material it uses.
/// </summary>
public sealed class MaterialBindless : IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderDevice _renderDevice;
    private readonly uint[] _mirror;
    private readonly SomeEngine.Graphics.Buffer _values;
    private readonly RangeAllocator _valueRanges;
    private readonly uint _descriptorSlotsPerShape;
    private readonly Dictionary<long, MaterialState> _materials =
        [];
    private readonly Dictionary<long, Dictionary<uint, TextureRequest>> _textureRequests =
        [];
    private readonly Dictionary<DescriptorSlotDesc, DescriptorPool> _descriptorPools = [];
    private readonly List<ValueRange> _dirty = [];
    private readonly HashSet<GraphPassId> _frameConsumers = [];
    private readonly HashSet<GraphPassId> _frameBoundPasses = [];
    private readonly List<GraphPassId> _frameUploads = [];
    private readonly List<ValueRange> _uploadRanges = [];
    private ulong _frameIdentity;
    private GraphBufferId _frameValues;
    private GraphBufferSrvId _frameValuesView;
    private bool _valuesInitialized;
    private bool _disposed;

    public MaterialBindless(
        RenderDevice renderDevice,
        uint maximumValueSlots,
        uint descriptorSlotsPerShape = 1024)
    {
        _renderDevice = renderDevice ?? throw new ArgumentNullException(nameof(renderDevice));
        _backend = renderDevice.Backend;
        _device = renderDevice.Device;
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
        AddDirty(0, maximumValueSlots);
    }

    public SomeEngine.Graphics.Buffer ValuesBuffer => _values;

    /// <summary>
    /// Returns this extension's compact value offset for one Material slot. Resolve this while
    /// building the shader/pipeline binding; render code should use the resulting constant.
    /// </summary>
    public uint GetValueOffset(MaterialData material, uint materialSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        foreach (ref readonly MaterialResourceBinding binding in ResolveMaterialBindings(material).AsSpan())
        {
            if (binding.MaterialSlot == materialSlot)
                return binding.ValueOffset;
        }
        throw new ArgumentOutOfRangeException(
            nameof(materialSlot),
            materialSlot,
            "The Material slot does not contribute a MaterialBindless value.");
    }

    public void SetTextureSrv(
        MaterialData material,
        uint materialSlot,
        in TextureSubresourceRange range,
        Format? format = null,
        SomeEngine.Graphics.TextureViewDimension? dimension = null) =>
        SetTextureRequest(
            material,
            materialSlot,
            new TextureRequest(MaterialResourceKind.TextureSrv, range, format, dimension));

    public void SetTextureUav(
        MaterialData material,
        uint materialSlot,
        in TextureSubresourceRange range,
        Format? format = null,
        SomeEngine.Graphics.TextureViewDimension? dimension = null) =>
        SetTextureRequest(
            material,
            materialSlot,
            new TextureRequest(MaterialResourceKind.TextureUav, range, format, dimension));

    private void SetTextureRequest(
        MaterialData material,
        uint materialSlot,
        in TextureRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        if (_materials.ContainsKey(material.MaterialId))
        {
            throw new InvalidOperationException(
                "MaterialBindless texture bindings must be fixed before the material is first used.");
        }
        IReadOnlyList<MaterialPropertyDefinition> definitions = material.Properties;
        if (materialSlot >= definitions.Count)
            throw new ArgumentOutOfRangeException(nameof(materialSlot));
        if (!typeof(SomeEngine.Resources.Texture).IsAssignableFrom(definitions[checked((int)materialSlot)].ValueType))
        {
            throw new InvalidOperationException(
                $"Material slot {materialSlot} is not a Texture slot.");
        }
        if (!_textureRequests.TryGetValue(material.MaterialId, out Dictionary<uint, TextureRequest>? requests))
        {
            requests = [];
            _textureRequests.Add(material.MaterialId, requests);
        }
        requests[materialSlot] = request;
    }

    public uint Use(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        MaterialData material,
        PipelineSync sync)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        EnsureFrame(ref frame);
        GraphPassId consumer = pass.Id;
        if (_frameConsumers.Add(consumer))
            foreach (GraphPassId upload in _frameUploads)
                frame.OrderAfter(consumer, upload);

        if (!_materials.TryGetValue(material.MaterialId, out MaterialState? state))
        {
            MaterialResourceBinding[] bindings = ResolveMaterialBindings(material);
            uint count = checked((uint)bindings.Length);
            uint baseSlot = _valueRanges.Allocate(count);
            state = new MaterialState(baseSlot, bindings);
            _materials.Add(material.MaterialId, state);
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
        EnsureFrame(ref frame);
        FlushDirty(ref frame);
    }

    public void Remove(ref RenderGraphFrame frame, MaterialData material)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(material);
        _textureRequests.Remove(material.MaterialId);
        if (_materials.Remove(material.MaterialId, out MaterialState? state))
        {
            frame.RetireAfterFrame(new DisposeAction(() =>
                _renderDevice.RetireAfterTrackedSubmissions(
                    new DisposeAction(() => Release(state)))));
        }
    }

    private void UpdateMaterial(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        MaterialData material,
        MaterialState state,
        PipelineSync sync)
    {
        for (int valueIndex = 0; valueIndex < state.Bindings.Length; valueIndex++)
        {
            ref readonly MaterialResourceBinding binding = ref state.Bindings[valueIndex];
            uint materialSlot = binding.MaterialSlot;
            object? raw = material.GetValue(materialSlot);
            switch (binding.Kind)
            {
                case MaterialResourceKind.TextureSrv:
                {
                    EngineTexture? texture = raw switch
                    {
                        null => null,
                        EngineTexture value => value,
                        _ => throw new InvalidOperationException(
                            $"Material slot {materialSlot} declares Texture but contains {raw.GetType().FullName}."),
                    };
                    TextureUse use = PrepareTextureSrv(
                        ref frame,
                        ref pass,
                        texture,
                        binding.TextureRequest,
                        sync);
                    UpdateTextureSlot(
                        ref frame,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        texture,
                        use);
                    break;
                }
                case MaterialResourceKind.TextureUav:
                {
                    EngineTexture texture = raw as EngineTexture ?? throw new InvalidOperationException(
                        $"Material slot {materialSlot} must contain a Texture for writable A access.");
                    TextureUse use = PrepareTextureUav(
                        ref frame,
                        ref pass,
                        texture,
                        binding.TextureRequest,
                        sync);
                    UpdateTextureSlot(
                        ref frame,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        texture,
                        use);
                    break;
                }
                case MaterialResourceKind.Sampler:
                    if (raw is not SamplerDesc sampler)
                    {
                        throw new InvalidOperationException(
                            $"Material slot {materialSlot} declares SamplerDesc but has no sampler value.");
                    }
                    UpdateSamplerSlot(
                        ref frame,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        sampler);
                    break;
                case MaterialResourceKind.BufferSrv:
                    UpdateDeviceResourceSlot(
                        ref frame,
                        ref pass,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        raw as BufferSrv ?? throw InvalidResourceValue(materialSlot, raw, nameof(BufferSrv)),
                        sync);
                    break;
                case MaterialResourceKind.BufferUav:
                    UpdateDeviceResourceSlot(
                        ref frame,
                        ref pass,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        raw as BufferUav ?? throw InvalidResourceValue(materialSlot, raw, nameof(BufferUav)),
                        sync);
                    break;
                case MaterialResourceKind.ConstantBuffer:
                    UpdateDeviceResourceSlot(
                        ref frame,
                        ref pass,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        raw as BufferCbv ?? throw InvalidResourceValue(materialSlot, raw, nameof(BufferCbv)),
                        sync);
                    break;
                case MaterialResourceKind.AccelerationStructure:
                    UpdateDeviceResourceSlot(
                        ref frame,
                        ref pass,
                        material,
                        state,
                        valueIndex,
                        materialSlot,
                        raw as AccelerationStructureSrv ?? throw InvalidResourceValue(
                            materialSlot,
                            raw,
                            nameof(AccelerationStructureSrv)),
                        sync);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown MaterialBindless resource kind {binding.Kind}.");
            }
        }
    }

    private void UpdateTextureSlot(
        ref RenderGraphFrame frame,
        MaterialData material,
        MaterialState state,
        int valueIndex,
        uint materialSlot,
        EngineTexture? texture,
        in TextureUse use)
    {
        SlotState? current = state.Slots[valueIndex];
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

        ulong slotRevision = material.GetPropertyRevision(materialSlot);
        if (current is null
            || !ReferenceEquals(current.Texture, texture)
            || current.TextureRevision != (texture?.Revision ?? 0)
            || !ReferenceEquals(current.TextureView, use.View)
            || current.SlotRevision != slotRevision
            || current.Allocation != allocation)
        {
            _backend.WriteDescriptor(
                allocation.Pool.Table,
                allocation.Slot,
                use.Binding);
            WriteValue(
                checked(state.Base + state.Bindings[valueIndex].ValueOffset),
                allocation.Index.Value);
            state.Slots[valueIndex] = new SlotState
            {
                Allocation = allocation,
                SlotRevision = slotRevision,
                Texture = texture,
                TextureRevision = texture?.Revision ?? 0,
                TextureView = use.View,
            };
        }
    }

    private void UpdateSamplerSlot(
        ref RenderGraphFrame frame,
        MaterialData material,
        MaterialState state,
        int valueIndex,
        uint materialSlot,
        in SamplerDesc description)
    {
        SlotState? current = state.Slots[valueIndex];
        ulong slotRevision = material.GetPropertyRevision(materialSlot);
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
        WriteValue(
            checked(state.Base + state.Bindings[valueIndex].ValueOffset),
            allocation.Index.Value);
        state.Slots[valueIndex] = new SlotState
        {
            Allocation = allocation,
            SlotRevision = slotRevision,
            Sampler = sampler,
            SamplerDescription = description,
        };
    }

    private void UpdateDeviceResourceSlot(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        MaterialData material,
        MaterialState state,
        int valueIndex,
        uint materialSlot,
        DeviceResource resource,
        PipelineSync sync)
    {
        if (!ReferenceEquals(resource.Device, _device))
        {
            throw new InvalidOperationException(
                $"Material slot {materialSlot} references a resource from another Device.");
        }

        DescriptorSlotDesc shape;
        ResourceBinding descriptor;
        switch (resource)
        {
            case BufferSrv srv:
            {
                BufferSrvDesc description = srv.Description;
                GraphBufferId buffer = ImportBuffer(ref frame, description.Buffer);
                _ = pass.Read(
                    buffer,
                    description.Range,
                    sync,
                    ResourceAccess.ShaderResource);
                shape = new DescriptorSlotDesc(
                    ResourceBindingType.BufferSrv,
                    description.Format,
                    description.StructureStride);
                descriptor = ResourceBinding.ReadOnlyBuffer(srv);
                break;
            }
            case BufferUav uav:
            {
                BufferUavDesc description = uav.Description;
                GraphBufferId buffer = ImportBuffer(ref frame, description.Buffer);
                _ = pass.ReadWrite(
                    buffer,
                    description.Range,
                    sync,
                    ResourceAccess.UnorderedAccess);
                if (description.CounterBuffer is { } counter)
                {
                    GraphBufferId counterBuffer = ImportBuffer(ref frame, counter);
                    _ = pass.ReadWrite(
                        counterBuffer,
                        new BufferRange(description.CounterOffset, sizeof(uint)),
                        sync,
                        ResourceAccess.UnorderedAccess);
                }
                shape = new DescriptorSlotDesc(
                    ResourceBindingType.BufferUav,
                    description.Format,
                    description.StructureStride,
                    HasCounter: description.CounterBuffer is not null);
                descriptor = ResourceBinding.WritableBuffer(uav);
                break;
            }
            case BufferCbv cbv:
            {
                BufferCbvDesc description = cbv.Description;
                GraphBufferId buffer = ImportBuffer(ref frame, description.Buffer);
                _ = pass.Read(
                    buffer,
                    description.Range,
                    sync,
                    ResourceAccess.ConstantBuffer);
                shape = new DescriptorSlotDesc(ResourceBindingType.ConstantBuffer);
                descriptor = ResourceBinding.ConstantBuffer(cbv);
                break;
            }
            case AccelerationStructureSrv accelerationStructureView:
            {
                AccelerationStructure resourceValue = accelerationStructureView.Resource;
                GraphBufferId storage = ImportBuffer(ref frame, resourceValue.Info.Storage);
                _ = pass.Read(
                    storage,
                    resourceValue.Info.StorageRange,
                    sync,
                    ResourceAccess.RayTracingAccelerationStructureRead);
                shape = new DescriptorSlotDesc(ResourceBindingType.AccelerationStructure);
                descriptor = ResourceBinding.AccelerationStructure(accelerationStructureView);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Material slot {materialSlot} uses unsupported device resource type {resource.GetType().FullName}.");
        }

        SlotState? current = state.Slots[valueIndex];
        DescriptorAllocation allocation;
        if (current?.Allocation is not { } existing || existing.Pool.Shape != shape)
        {
            allocation = AllocateDescriptor(shape);
            if (current?.Allocation is { } retired)
            {
                frame.RetireAfterSubmittedFrames(
                    new DisposeAction(() => retired.Pool.Free(retired.Slot)));
            }
        }
        else
        {
            allocation = existing;
        }

        ulong slotRevision = material.GetPropertyRevision(materialSlot);
        if (current is not null &&
            current.SlotRevision == slotRevision &&
            ReferenceEquals(current.ResourceView, resource) &&
            current.Allocation == allocation)
        {
            return;
        }

        _backend.WriteDescriptor(allocation.Pool.Table, allocation.Slot, descriptor);
        WriteValue(
            checked(state.Base + state.Bindings[valueIndex].ValueOffset),
            allocation.Index.Value);
        state.Slots[valueIndex] = new SlotState
        {
            Allocation = allocation,
            SlotRevision = slotRevision,
            ResourceView = resource,
        };
    }

    private static GraphBufferId ImportBuffer(ref RenderGraphFrame frame, SomeEngine.Graphics.Buffer buffer)
    {
        BufferRange range = new(0, buffer.Info.Size);
        return frame.Import(
            buffer,
            [new BufferBoundaryState(
                range,
                buffer.InitialSync,
                buffer.InitialAccess,
                ResourceContentState.Defined)]);
    }

    private MaterialResourceBinding[] ResolveMaterialBindings(MaterialData material)
    {
        IReadOnlyList<MaterialPropertyDefinition> definitions = material.Properties;
        var result = new List<MaterialResourceBinding>(definitions.Count);
        _textureRequests.TryGetValue(material.MaterialId, out Dictionary<uint, TextureRequest>? textureRequests);
        for (uint slot = 0; slot < definitions.Count; slot++)
        {
            Type type = definitions[checked((int)slot)].ValueType;
            TextureRequest textureRequest = default;
            MaterialResourceKind? kind =
                typeof(SomeEngine.Resources.Texture).IsAssignableFrom(type)
                    ? ResolveTextureKind(slot, textureRequests, out textureRequest) :
                type == typeof(SamplerDesc) ? MaterialResourceKind.Sampler :
                typeof(BufferSrv).IsAssignableFrom(type) ? MaterialResourceKind.BufferSrv :
                typeof(BufferUav).IsAssignableFrom(type) ? MaterialResourceKind.BufferUav :
                typeof(BufferCbv).IsAssignableFrom(type) ? MaterialResourceKind.ConstantBuffer :
                typeof(AccelerationStructureSrv).IsAssignableFrom(type) ? MaterialResourceKind.AccelerationStructure :
                null;
            if (kind is { } resolved)
            {
                result.Add(new MaterialResourceBinding(
                    slot,
                    checked((uint)result.Count),
                    resolved,
                    textureRequest));
            }
        }
        return [.. result];

        static MaterialResourceKind ResolveTextureKind(
            uint slot,
            Dictionary<uint, TextureRequest>? requests,
            out TextureRequest request)
        {
            if (requests is not null && requests.TryGetValue(slot, out request))
                return request.Kind;
            request = new TextureRequest(MaterialResourceKind.TextureSrv, null, null, null);
            return MaterialResourceKind.TextureSrv;
        }
    }

    private static InvalidOperationException InvalidResourceValue(
        uint materialSlot,
        object? value,
        string expected) =>
        new(
            $"Material slot {materialSlot} expects {expected} but contains " +
            $"{value?.GetType().FullName ?? "null"}.");

    private TextureUse PrepareTextureSrv(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        EngineTexture? asset,
        in TextureRequest request,
        PipelineSync sync)
    {
        SomeEngine.Graphics.Texture resource;
        TextureSrv view;
        if (asset is null)
        {
            resource = _renderDevice.PrepareMissingTexture(ref frame, pass.Id);
            view = _renderDevice.MissingTextureSrv;
        }
        else
        {
            resource = _renderDevice.PrepareTexture(ref frame, pass.Id, asset);
            view = _renderDevice.GetTextureSrv(
                asset,
                request.Range,
                request.Format,
                request.Dimension);
        }

        TextureSrvDesc description = view.Description;
        GraphTextureId graphTexture = frame.GetImported(resource);
        GraphTextureSrvId graphView = frame.CreateTextureSrv(
            graphTexture,
            description.Range,
            description.Format,
            description.Dimension,
            "MaterialBindless Texture SRV");
        _ = pass.Read(graphView, sync, TextureLayout.ShaderResource);
        DescriptorSlotDesc descriptor = new(
            ResourceBindingType.TextureSrv,
            description.Format,
            TextureDimension: description.Dimension,
            Aspects: description.Range.Aspects);
        return new TextureUse(view, ResourceBinding.SampledTexture(view), descriptor);
    }

    private TextureUse PrepareTextureUav(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        EngineTexture asset,
        in TextureRequest request,
        PipelineSync sync)
    {
        if (request.Range is not { } range)
            throw new InvalidOperationException("A MaterialBindless texture UAV requires an explicit subresource range.");
        SomeEngine.Graphics.Texture resource = _renderDevice.PrepareTexture(ref frame, pass.Id, asset);
        TextureUav view = _renderDevice.GetTextureUav(
            asset,
            range,
            request.Format,
            request.Dimension);
        TextureUavDesc description = view.Description;
        GraphTextureId graphTexture = frame.GetImported(resource);
        GraphTextureUavId graphView = frame.CreateTextureUav(
            graphTexture,
            description.Range,
            description.Format,
            description.Dimension,
            "MaterialBindless Texture UAV");
        _ = pass.ReadWrite(graphView, sync, layout: TextureLayout.UnorderedAccess);
        DescriptorSlotDesc descriptor = new(
            ResourceBindingType.TextureUav,
            description.Format,
            TextureDimension: description.Dimension,
            Aspects: description.Range.Aspects);
        return new TextureUse(view, ResourceBinding.StorageTexture(view), descriptor);
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
        if (_uploadRanges.Count != 0)
            throw new InvalidOperationException("Material uploads still belong to an unfinished frame.");
        _frameIdentity = frame.FrameIdentity;
        frame.ObserveExecution(this);
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
            GraphBufferSlice upload = frame.Upload(
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
                        value.Upload.Buffer,
                        value.Upload.Range,
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
                        commands.GetBuffer(value.Upload.Buffer),
                        value.Upload.Range.Offset,
                        commands.GetBuffer(value.Destination),
                        value.DestinationOffset,
                        value.Size));
                });
            _frameUploads.Add(copy);
            _uploadRanges.Add(dirty);
            foreach (GraphPassId consumer in _frameConsumers)
                frame.OrderAfter(consumer, copy);
        }
        _dirty.Clear();
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!result.SubmittedCompletions.IsEmpty)
            _renderDevice.TrackSubmission(result.SubmittedCompletions);
        bool allSubmitted = true;
        for (int i = 0; i < _uploadRanges.Count; i++)
        {
            if (result.TryGetPassCompletion(_frameUploads[i], out _))
                continue;
            ValueRange range = _uploadRanges[i];
            AddDirty(range.Start, range.Count);
            allSubmitted = false;
        }
        if (allSubmitted && _uploadRanges.Count != 0)
            _valuesInitialized = true;
        _uploadRanges.Clear();
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

    private void Release(MaterialState state)
    {
        _valueRanges.Free(state.Base, checked((uint)state.Bindings.Length));
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
        _materials.Clear();
        _textureRequests.Clear();
        foreach (DescriptorPool pool in _descriptorPools.Values)
            pool.Dispose();
        _descriptorPools.Clear();
        _values.Dispose();
    }

    private sealed class MaterialState
    {
        internal MaterialState(uint baseSlot, MaterialResourceBinding[] bindings)
        {
            Base = baseSlot;
            Bindings = bindings;
            Slots = new SlotState?[bindings.Length];
        }

        internal uint Base { get; }
        internal MaterialResourceBinding[] Bindings { get; }
        internal SlotState?[] Slots { get; }
    }

    private sealed class SlotState
    {
        internal DescriptorAllocation? Allocation { get; init; }
        internal ulong SlotRevision { get; init; }
        internal EngineTexture? Texture { get; init; }
        internal ulong TextureRevision { get; init; }
        internal DeviceResource? TextureView { get; init; }
        internal Sampler? Sampler { get; init; }
        internal SamplerDesc SamplerDescription { get; init; }
        internal DeviceResource? ResourceView { get; init; }
    }

    private enum MaterialResourceKind : byte
    {
        TextureSrv,
        TextureUav,
        Sampler,
        BufferSrv,
        BufferUav,
        ConstantBuffer,
        AccelerationStructure,
    }

    private readonly record struct MaterialResourceBinding(
        uint MaterialSlot,
        uint ValueOffset,
        MaterialResourceKind Kind,
        TextureRequest TextureRequest);

    private readonly record struct TextureRequest(
        MaterialResourceKind Kind,
        TextureSubresourceRange? Range,
        Format? Format,
        SomeEngine.Graphics.TextureViewDimension? Dimension);

    private readonly record struct TextureUse(
        DeviceResource View,
        ResourceBinding Binding,
        DescriptorSlotDesc Descriptor);

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

    private readonly record struct ValueRange(uint Start, uint Count);

    private readonly record struct ValuesUploadPass(
        GraphBufferSlice Upload,
        GraphBufferId Destination,
        ulong DestinationOffset,
        ulong Size);
}
