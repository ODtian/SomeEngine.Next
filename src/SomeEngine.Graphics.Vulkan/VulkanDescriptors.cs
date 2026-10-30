namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal PersistentParameterBindings CreatePersistentParameterBindings(
        RhiDevice device,
        Pipeline pipeline,
        in ParameterBlockBindings bindings,
        string? label = null)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipeline nativePipeline = RequirePipeline(nativeDevice, pipeline, nameof(pipeline));
        VulkanBlockLayout block = nativePipeline.Layout.GetBlock(bindings.Layout);
        VulkanDescriptorGeneration generation = CreateDescriptorGeneration(
            nativeDevice,
            nativePipeline.Layout,
            block,
            bindings);
        nativePipeline.RetainNative();
        VulkanPersistentParameterBindings? result = null;
        try
        {
            result = new VulkanPersistentParameterBindings(
                nativeDevice,
                nativePipeline,
                block,
                generation,
                label);
            return RegisterChildOrDispose(nativeDevice, result);
        }
        catch
        {
            if (result is null)
            {
                nativePipeline.ReleaseNative();
                generation.ReleaseNative();
            }
            throw;
        }
    }

    internal void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings)
    {
        if (destination is not VulkanPersistentParameterBindings native ||
            native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The bindings belong to a different graphics backend.", nameof(destination));
        native.ThrowIfDisposed();
        if (bindings.Layout != native.Layout)
            throw new ArgumentException("An update must preserve the Slang parameter layout.", nameof(bindings));
        VulkanDescriptorGeneration generation = CreateDescriptorGeneration(
            device,
            native.Pipeline.Layout,
            native.Block,
            bindings);
        native.Publish(generation);
    }

    internal DescriptorTable CreateDescriptorTable(
        RhiDevice device,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        if (nodeIndex is not uint.MaxValue and not 0)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        DescriptorTableType type = slots.IsEmpty || slots[0].Type != ResourceBindingType.Sampler
            ? DescriptorTableType.Resource
            : DescriptorTableType.Sampler;
        VulkanDescriptorRange range = nativeDevice.BindlessPublisher.Reserve(
            type,
            checked((uint)slots.Length));
        VulkanDescriptorTable? table = null;
        bool publisherRegistered = false;
        try
        {
            table = new VulkanDescriptorTable(
                nativeDevice,
                type,
                range,
                slots,
                label);
            nativeDevice.BindlessPublisher.Register(table);
            publisherRegistered = true;
            return RegisterChildOrDispose(nativeDevice, table);
        }
        catch
        {
            if (publisherRegistered)
                table!.Dispose();
            else
                nativeDevice.BindlessPublisher.CancelReservation(type, range);
            throw;
        }
    }

    internal DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        VulkanDescriptorTable native = RequireDescriptorTable(table, nameof(table));
        if (slot >= native.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return new DescriptorIndex(native, checked(native.FirstIndex + slot));
    }

    internal void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value)
    {
        VulkanDescriptorTable native = RequireDescriptorTable(table, nameof(table));
        native.Write(slot, value);
    }

    internal void PublishDescriptors(
        RhiDevice device,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (nodeIndex is not uint.MaxValue and not 0)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        nativeDevice.BindlessPublisher.Publish(cancellationToken);
    }

    private VulkanDescriptorGeneration CreateDescriptorGeneration(
        VulkanDevice device,
        VulkanPipelineLayoutState pipelineLayout,
        VulkanBlockLayout block,
        in ParameterBlockBindings bindings)
    {
        VulkanDescriptorSlot[] slots = ValidateParameterBindings(device, block, bindings);
        var allocations = new VulkanDescriptorSetAllocation[block.SetIndices.Length];
        var sets = new VkDescriptorSet[block.SetIndices.Length];
        VulkanUniformBuffer? uniform = null;
        var retained = new List<IVulkanRetained>();
        try
        {
            for (int index = 0; index < block.SetIndices.Length; index++)
            {
                uint set = block.SetIndices[index];
                allocations[index] = device.DescriptorAllocator.Allocate(
                    pipelineLayout.SetLayouts[checked((int)set)]);
                sets[index] = allocations[index].Set;
            }
            byte[] pushConstants = [];
            DescriptorBufferInfo uniformInfo = default;
            if (block.Ordinary is VulkanOrdinaryBinding ordinary)
            {
                if (ordinary.PushConstants)
                {
                    pushConstants = bindings.OrdinaryData.ToArray();
                }
                else
                {
                    uniform = new VulkanUniformBuffer(device, bindings.OrdinaryData);
                    uniformInfo = new DescriptorBufferInfo(uniform.Native, 0, ordinary.Size);
                }
            }
            for (int index = 0; index < slots.Length; index++)
            {
                ResourceBinding value = bindings.Resources[index];
                if (value.Value is IVulkanRetained dependency)
                {
                    dependency.RetainNative();
                    retained.Add(dependency);
                }
            }
            int writeCount = slots.Length + (block.Ordinary is { PushConstants: false } ? 1 : 0);
            WriteParameterDescriptors(
                device,
                block,
                sets,
                slots,
                bindings.Resources,
                uniformInfo,
                new WriteDescriptorSet[writeCount],
                new VulkanDescriptorData[writeCount]);
            return new VulkanDescriptorGeneration(
                device,
                allocations,
                CreateBoundDescriptorSets(block.SetIndices, sets),
                uniform,
                retained.ToArray(),
                pushConstants,
                block);
        }
        catch
        {
            for (int index = retained.Count - 1; index >= 0; index--)
                retained[index].ReleaseNative();
            uniform?.Release();
            foreach (VulkanDescriptorSetAllocation allocation in allocations)
                if (allocation.Set.Handle != 0)
                    device.DescriptorAllocator.Free(allocation);
            throw;
        }
    }

    private static VulkanBoundDescriptorSet[] CreateBoundDescriptorSets(
        ReadOnlySpan<uint> setIndices,
        ReadOnlySpan<VkDescriptorSet> sets)
    {
        var bindings = new VulkanBoundDescriptorSet[sets.Length];
        for (int index = 0; index < bindings.Length; index++)
            bindings[index] = new VulkanBoundDescriptorSet(setIndices[index], sets[index]);
        return bindings;
    }

    private void BindTransientDescriptorGeneration(
        VulkanCommandContext command,
        VulkanPipeline pipeline,
        VulkanBlockLayout block,
        in ParameterBlockBindings bindings)
    {
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanDescriptorSlot[] slots = ValidateParameterBindings(
            device,
            block,
            bindings);
        VulkanDescriptorArena arena = command.RecordingDescriptorArena;
        ReadOnlySpan<uint> setIndices = block.SetIndices;
        Span<VkDescriptorSet> sets = arena.PrepareSetStorage(setIndices.Length);
        for (int index = 0; index < sets.Length; index++)
        {
            sets[index] = arena.Allocate(pipeline.Layout, setIndices[index]);
        }

        int writeCount = slots.Length + (block.Ordinary is { PushConstants: false } ? 1 : 0);
        arena.PrepareDescriptorStorage(
            writeCount,
            out Span<WriteDescriptorSet> writes,
            out Span<VulkanDescriptorData> descriptorData);
        DescriptorBufferInfo uniformInfo = default;
        if (block.Ordinary is VulkanOrdinaryBinding ordinary)
        {
            if (ordinary.PushConstants)
            {
                fixed (byte* data = bindings.OrdinaryData)
                {
                    Api.CmdPushConstants(
                        command.NativeRecording,
                        pipeline.Layout.Native,
                        ordinary.Stages,
                        ordinary.PushConstantOffset,
                        ordinary.Size,
                        data);
                }
            }
            else
            {
                arena.WriteUniform(
                    bindings.OrdinaryData,
                    out VkBuffer uniform,
                    out ulong offset);
                uniformInfo = new DescriptorBufferInfo(uniform, offset, ordinary.Size);
            }
        }

        for (int index = 0; index < slots.Length; index++)
        {
            ResourceBinding value = bindings.Resources[index];
            if (value.Value is IVulkanRetained dependency)
                command.Capture(dependency);
        }
        WriteParameterDescriptors(
            device,
            block,
            sets,
            slots,
            bindings.Resources,
            uniformInfo,
            writes,
            descriptorData);

        PipelineBindPoint bindPoint = ToBindPoint(pipeline.Type);
        for (int index = 0; index < sets.Length; index++)
        {
            VkDescriptorSet set = sets[index];
            Api.CmdBindDescriptorSets(
                command.NativeRecording,
                bindPoint,
                pipeline.Layout.Native,
                setIndices[index],
                1,
                &set,
                0,
                null);
        }

    }

    private static void WriteParameterDescriptors(
        VulkanDevice device,
        VulkanBlockLayout block,
        ReadOnlySpan<VkDescriptorSet> sets,
        ReadOnlySpan<VulkanDescriptorSlot> slots,
        ReadOnlySpan<ResourceBinding> resources,
        in DescriptorBufferInfo uniformInfo,
        Span<WriteDescriptorSet> writes,
        Span<VulkanDescriptorData> descriptorData)
    {
        if (writes.IsEmpty)
            return;
        // Vulkan consumes every descriptor pointer before these arrays leave the fixed scope.
        fixed (WriteDescriptorSet* writePointer = writes)
        fixed (VulkanDescriptorData* dataPointer = descriptorData)
        {
            int writeIndex = 0;
            if (block.Ordinary is { PushConstants: false } ordinary)
            {
                dataPointer[writeIndex].Buffer = uniformInfo;
                writePointer[writeIndex] = CreateNativeDescriptor(
                    FindDescriptorSet(block.SetIndices, sets, ordinary.Set),
                    ordinary.Binding,
                    0,
                    DescriptorType.UniformBuffer,
                    &dataPointer[writeIndex]);
                writeIndex++;
            }
            for (int index = 0; index < slots.Length; index++, writeIndex++)
            {
                ref readonly VulkanDescriptorSlot slot = ref slots[index];
                writePointer[writeIndex] = CreateResourceDescriptor(
                    device,
                    FindDescriptorSet(block.SetIndices, sets, slot.Set),
                    slot,
                    resources[index],
                    &dataPointer[writeIndex]);
            }
            device.Backend.Api.UpdateDescriptorSets(
                device.Native,
                checked((uint)writes.Length),
                writePointer,
                0,
                null);
        }
    }

    private static VkDescriptorSet FindDescriptorSet(
        ReadOnlySpan<uint> setIndices,
        ReadOnlySpan<VkDescriptorSet> sets,
        uint setIndex)
    {
        int index = setIndices.IndexOf(setIndex);
        if (index < 0)
            throw new InvalidOperationException("The Vulkan parameter block references an unallocated descriptor set.");
        return sets[index];
    }

    private void WriteResourceDescriptor(
        VulkanDevice device,
        VkDescriptorSet set,
        in VulkanDescriptorSlot slot,
        in ResourceBinding value)
    {
        VulkanDescriptorData data = default;
        WriteDescriptorSet write = CreateResourceDescriptor(device, set, slot, value, &data);
        Api.UpdateDescriptorSets(device.Native, 1, &write, 0, null);
    }

    private static WriteDescriptorSet CreateResourceDescriptor(
        VulkanDevice device,
        VkDescriptorSet set,
        in VulkanDescriptorSlot slot,
        in ResourceBinding value,
        VulkanDescriptorData* data)
    {
        *data = default;
        if (value.IsNull)
        {
            // A null buffer descriptor requires VK_WHOLE_SIZE (VUID 02999).
            data->Buffer.Range = Vk.WholeSize;
            if (slot.BindingType == ResourceBindingType.Sampler)
                throw new ArgumentException("Sampler bindings cannot be null.", nameof(value));
            if (!device.ExtendedFeatures.NullDescriptor)
            {
                throw new NotSupportedException(
                    "This Vulkan Device cannot provide typed null descriptors.");
            }
            return CreateNativeDescriptor(set, slot.Binding, slot.ArrayElement, slot.DescriptorType, data);
        }
        switch (value.Value)
        {
            case VulkanAccelerationStructureSrv acceleration:
                data->AccelerationStructure = acceleration.Native;
                break;
            case VulkanBuffer direct when value.Type == ResourceBindingType.ConstantBuffer:
                data->Buffer = new DescriptorBufferInfo(direct.Native, value.BufferRange.Offset, value.BufferRange.Size);
                break;
            case VulkanBufferCbv cbv:
            {
                VulkanBuffer buffer = (VulkanBuffer)cbv.Resource;
                BufferRange range = cbv.Description.Range.Resolve(buffer.Info.Size);
                data->Buffer = new DescriptorBufferInfo(buffer.Native, range.Offset, range.Size);
                break;
            }
            case VulkanBufferSrv srv:
                PrepareBufferDescriptor(srv.Resource, srv.Description.Range, srv.Native, slot.DescriptorType, ref data->Buffer, ref data->TexelView);
                break;
            case VulkanBufferUav uav:
                PrepareBufferDescriptor(uav.Resource, uav.Description.Range, uav.Native, slot.DescriptorType, ref data->Buffer, ref data->TexelView);
                break;
            case VulkanTextureSrv srv:
                data->Image = new DescriptorImageInfo(default, srv.Native, ImageLayout.ShaderReadOnlyOptimal);
                break;
            case VulkanTextureUav uav:
                data->Image = new DescriptorImageInfo(default, uav.Native, ImageLayout.General);
                break;
            case VulkanSampler sampler:
                data->Image = new DescriptorImageInfo(sampler.Native, default, ImageLayout.Undefined);
                break;
            default:
                throw new NotSupportedException($"Vulkan descriptor value {value.Value?.GetType().Name} is not implemented.");
        }
        return CreateNativeDescriptor(
            set,
            slot.Binding,
            slot.ArrayElement,
            slot.DescriptorType,
            data);
    }

    private static void PrepareBufferDescriptor(
        RhiBuffer publicBuffer,
        in BufferRange publicRange,
        VkBufferView view,
        DescriptorType descriptorType,
        ref DescriptorBufferInfo bufferInfo,
        ref VkBufferView texelView)
    {
        VulkanBuffer buffer = (VulkanBuffer)publicBuffer;
        BufferRange range = publicRange.Resolve(buffer.Info.Size);
        if (descriptorType is DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer)
            texelView = view;
        else
            bufferInfo = new DescriptorBufferInfo(buffer.Native, range.Offset, range.Size);
    }

    private static VulkanDescriptorSlot[] ValidateParameterBindings(
        VulkanDevice device,
        VulkanBlockLayout block,
        in ParameterBlockBindings bindings)
    {
        if (bindings.Layout != block.ReflectedLayout)
            throw new ArgumentException("The Slang parameter layout does not match the Pipeline block.", nameof(bindings));
        uint ordinarySize = block.Ordinary?.Size ?? 0;
        if (bindings.OrdinaryData.Length != ordinarySize)
            throw new ArgumentException($"The parameter block requires {ordinarySize} ordinary-data bytes.", nameof(bindings));
        VulkanDescriptorSlot[] slots = block.ResolveSlots(bindings.Resources);
        for (int index = 0; index < slots.Length; index++)
        {
            if (!bindings.Resources[index].IsNull)
                continue;
            if (slots[index].BindingType == ResourceBindingType.Sampler)
                throw new ArgumentException("Sampler bindings cannot be null.", nameof(bindings));
            if (!device.ExtendedFeatures.NullDescriptor)
            {
                throw new NotSupportedException(
                    "This Vulkan Device cannot provide typed null descriptors.");
            }
        }
        return slots;
    }

    private static WriteDescriptorSet CreateNativeDescriptor(
        VkDescriptorSet set,
        uint binding,
        uint arrayElement,
        DescriptorType type,
        VulkanDescriptorData* data)
    {
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = arrayElement,
            DescriptorCount = 1,
            DescriptorType = type,
            PBufferInfo = type is DescriptorType.UniformBuffer or DescriptorType.StorageBuffer
                ? &data->Buffer
                : null,
            PImageInfo = type is DescriptorType.SampledImage or DescriptorType.StorageImage or
                DescriptorType.Sampler or DescriptorType.InputAttachment or DescriptorType.CombinedImageSampler
                ? &data->Image
                : null,
            PTexelBufferView = type is DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                ? &data->TexelView
                : null,
        };
        if (type == DescriptorType.AccelerationStructureKhr)
        {
            data->Acceleration = new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &data->AccelerationStructure,
            };
            write.PNext = &data->Acceleration;
        }
        return write;
    }

    private struct VulkanDescriptorData
    {
        internal DescriptorBufferInfo Buffer;
        internal DescriptorImageInfo Image;
        internal VkBufferView TexelView;
        internal WriteDescriptorSetAccelerationStructureKHR Acceleration;
        internal VkAccelerationStructure AccelerationStructure;
    }

    private VulkanPipeline RequirePipeline(
        VulkanDevice device,
        Pipeline pipeline,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(pipeline, parameterName);
        if (pipeline is not VulkanPipeline native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The Pipeline belongs to a different Vulkan Device.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private VulkanDescriptorTable RequireDescriptorTable(DescriptorTable table, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(table, parameterName);
        if (table is not VulkanDescriptorTable native ||
            native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The DescriptorTable belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private sealed class VulkanDescriptorAllocator
    {
        private const uint SetsPerPool = 4096;
        private readonly VulkanDevice _device;
        private readonly object _gate = new();
        private readonly List<VkDescriptorPool> _pools = [];
        private VkDescriptorPool _active;

        internal VulkanDescriptorAllocator(VulkanDevice device) => _device = device;

        internal VulkanDescriptorSetAllocation Allocate(VkDescriptorSetLayout layout)
        {
            lock (_gate)
            {
                if (_active.Handle == 0)
                    _active = CreatePool();
                Result result = AllocateFrom(_active, layout, out VkDescriptorSet set);
                if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
                {
                    foreach (VkDescriptorPool pool in _pools)
                    {
                        if (pool.Handle == _active.Handle)
                            continue;
                        result = AllocateFrom(pool, layout, out set);
                        if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
                            continue;
                        _device.ThrowIfDeviceCallFailed(result, "vkAllocateDescriptorSets");
                        _active = pool;
                        return new VulkanDescriptorSetAllocation(pool, set);
                    }
                    _active = CreatePool();
                    result = AllocateFrom(_active, layout, out set);
                }
                _device.ThrowIfDeviceCallFailed(result, "vkAllocateDescriptorSets");
                return new VulkanDescriptorSetAllocation(_active, set);
            }
        }

        internal void Free(in VulkanDescriptorSetAllocation allocation)
        {
            lock (_gate)
            {
                VkDescriptorSet set = allocation.Set;
                Result result = _device.Backend.Api.FreeDescriptorSets(
                    _device.Native,
                    allocation.Pool,
                    1,
                    &set);
                _device.ThrowIfDeviceCallFailed(result, "vkFreeDescriptorSets");
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                foreach (VkDescriptorPool pool in _pools)
                    _device.Backend.Api.DestroyDescriptorPool(_device.Native, pool, null);
                _pools.Clear();
                _active = default;
            }
        }

        private Result AllocateFrom(
            VkDescriptorPool pool,
            VkDescriptorSetLayout layout,
            out VkDescriptorSet set)
        {
            set = default;
            DescriptorSetAllocateInfo allocate = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            fixed (VkDescriptorSet* pointer = &set)
                return _device.Backend.Api.AllocateDescriptorSets(_device.Native, &allocate, pointer);
        }

        private VkDescriptorPool CreatePool()
        {
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[8];
            sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, SetsPerPool * 4);
            sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, SetsPerPool * 8);
            sizes[2] = new DescriptorPoolSize(DescriptorType.UniformTexelBuffer, SetsPerPool * 2);
            sizes[3] = new DescriptorPoolSize(DescriptorType.StorageTexelBuffer, SetsPerPool * 2);
            sizes[4] = new DescriptorPoolSize(DescriptorType.SampledImage, SetsPerPool * 8);
            sizes[5] = new DescriptorPoolSize(DescriptorType.StorageImage, SetsPerPool * 4);
            sizes[6] = new DescriptorPoolSize(DescriptorType.Sampler, SetsPerPool * 4);
            uint sizeCount = 7;
            if (_device.TryGetCapability(out RayTracing? rayTracing) && rayTracing is not null)
                sizes[sizeCount++] = new DescriptorPoolSize(
                    DescriptorType.AccelerationStructureKhr,
                    SetsPerPool * 2);
            DescriptorPoolCreateInfo createInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = SetsPerPool,
                PoolSizeCount = sizeCount,
                PPoolSizes = sizes,
            };
            VkDescriptorPool pool = default;
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.CreateDescriptorPool(_device.Native, &createInfo, null, &pool),
                "vkCreateDescriptorPool");
            _pools.Add(pool);
            return pool;
        }
    }

    private readonly record struct VulkanDescriptorSetAllocation(
        VkDescriptorPool Pool,
        VkDescriptorSet Set);

    private readonly record struct VulkanTransientDescriptorSet(
        VulkanPipelineLayoutState Layout,
        uint SetIndex,
        VulkanDescriptorSetAllocation Allocation);

    private sealed class VulkanDescriptorArena
    {
        private const ulong DefaultUploadPageSize = 64 * 1024;

        private readonly VulkanDevice _device;
        private VulkanTransientDescriptorSet[] _sets = [];
        private int _setCount;
        private int _usedSetCount;
        private VkDescriptorSet[] _setScratch = [];
        private WriteDescriptorSet[] _descriptorWrites = [];
        private VulkanDescriptorData[] _descriptorData = [];
        private VulkanUploadPage[] _uploadPages = [];
        private int _uploadPageCount;

        internal VulkanDescriptorArena(VulkanDevice device) => _device = device;

        internal Span<VkDescriptorSet> PrepareSetStorage(int count)
        {
            EnsureArray(ref _setScratch, count);
            return _setScratch.AsSpan(0, count);
        }

        internal void PrepareDescriptorStorage(
            int count,
            out Span<WriteDescriptorSet> writes,
            out Span<VulkanDescriptorData> data)
        {
            EnsureArray(ref _descriptorWrites, count);
            EnsureArray(ref _descriptorData, count);
            writes = _descriptorWrites.AsSpan(0, count);
            data = _descriptorData.AsSpan(0, count);
        }

        internal VkDescriptorSet Allocate(VulkanPipelineLayoutState layout, uint setIndex)
        {
            if (_usedSetCount < _setCount)
            {
                VulkanTransientDescriptorSet existing = _sets[_usedSetCount];
                if (ReferenceEquals(existing.Layout, layout) && existing.SetIndex == setIndex)
                {
                    _usedSetCount++;
                    return existing.Allocation.Set;
                }
                ReleaseSets(_usedSetCount);
            }
            EnsureArray(ref _sets, checked(_setCount + 1));
            layout.RetainSetLayouts();
            VulkanDescriptorSetAllocation allocation;
            try
            {
                allocation = _device.DescriptorAllocator.Allocate(layout.SetLayouts[checked((int)setIndex)]);
            }
            catch
            {
                layout.ReleaseSetLayouts();
                throw;
            }
            _sets[_setCount++] = new VulkanTransientDescriptorSet(layout, setIndex, allocation);
            _usedSetCount++;
            return allocation.Set;
        }

        internal void WriteUniform(
            ReadOnlySpan<byte> data,
            out VkBuffer buffer,
            out ulong offset)
        {
            for (int index = 0; index < _uploadPageCount; index++)
            {
                if (_uploadPages[index].TryWrite(data, out offset))
                {
                    buffer = _uploadPages[index].Native;
                    return;
                }
            }

            EnsureArray(ref _uploadPages, checked(_uploadPageCount + 1));
            ulong required = AlignUp(
                Math.Max(checked((ulong)data.Length), 16),
                Math.Max(_device.MinimumUniformBufferOffsetAlignment, 16));
            var page = new VulkanUploadPage(
                _device,
                Math.Max(DefaultUploadPageSize, required));
            _uploadPages[_uploadPageCount++] = page;
            if (!page.TryWrite(data, out offset))
                throw new InvalidOperationException("A new Vulkan upload page could not contain its requested allocation.");
            buffer = page.Native;
        }

        internal void ReleaseUnusedSets() => ReleaseSets(_usedSetCount);

        internal void ResetForRecording()
        {
            _usedSetCount = 0;
            for (int index = 0; index < _uploadPageCount; index++)
                _uploadPages[index].Reset();
        }

        internal void Release()
        {
            ReleaseSets(0);
            _usedSetCount = 0;
            for (int index = _uploadPageCount - 1; index >= 0; index--)
                _uploadPages[index].Release();
            Array.Clear(_uploadPages, 0, _uploadPageCount);
            _uploadPageCount = 0;
        }

        private void ReleaseSets(int count)
        {
            while (_setCount > count)
            {
                int index = _setCount - 1;
                VulkanTransientDescriptorSet set = _sets[index];
                _device.DescriptorAllocator.Free(set.Allocation);
                _sets[index] = default;
                _setCount = index;
                set.Layout.ReleaseSetLayouts();
            }
        }

        private static void EnsureArray<T>(ref T[] storage, int count)
        {
            if (storage.Length >= count)
                return;
            int capacity = storage.Length == 0 ? 8 : checked(storage.Length * 2);
            Array.Resize(ref storage, Math.Max(capacity, count));
        }
    }

    private sealed class VulkanUploadPage
    {
        private readonly VulkanDevice _device;
        private VulkanMemoryBlock? _memory;
        private VkBuffer _native;
        private readonly ulong _capacity;
        private ulong _offset;

        internal VulkanUploadPage(VulkanDevice device, ulong capacity)
        {
            _device = device;
            _capacity = capacity;
            BufferCreateInfo createInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = capacity,
                Usage = BufferUsageFlags.UniformBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            VkBuffer native = default;
            Result result = device.Backend.Api.CreateBuffer(
                device.Native,
                &createInfo,
                null,
                &native);
            device.ThrowIfDeviceCallFailed(result, "vkCreateBuffer(parameter upload page)");
            _native = native;
            try
            {
                Silk.NET.Vulkan.MemoryRequirements requirements;
                device.Backend.Api.GetBufferMemoryRequirements(
                    device.Native,
                    _native,
                    &requirements);
                _memory = device.AllocateMemory(
                    requirements.Size,
                    requirements.MemoryTypeBits,
                    MemoryType.Upload,
                    deviceAddress: false);
                device.ThrowIfDeviceCallFailed(
                    device.Backend.Api.BindBufferMemory(
                        device.Native,
                        _native,
                        _memory.Native,
                        0),
                    "vkBindBufferMemory(parameter upload page)");
            }
            catch
            {
                Release();
                throw;
            }
        }

        internal VkBuffer Native => _native;

        internal bool TryWrite(ReadOnlySpan<byte> data, out ulong offset)
        {
            ulong alignment = Math.Max(
                _device.MinimumUniformBufferOffsetAlignment,
                16);
            ulong start = AlignUp(_offset, alignment);
            ulong end = checked(start + (ulong)data.Length);
            if (end > _capacity)
            {
                offset = 0;
                return false;
            }
            VulkanMemoryBlock memory = _memory
                ?? throw new ObjectDisposedException(nameof(VulkanUploadPage));
            data.CopyTo(new Span<byte>(
                (byte*)memory.Mapped + checked((nint)start),
                data.Length));
            if (!memory.Coherent && !data.IsEmpty)
            {
                ulong atom = _device.NonCoherentAtomSize;
                ulong flushOffset = start / atom * atom;
                ulong flushEnd = Math.Min(AlignUp(end, atom), memory.Size);
                MappedMemoryRange range = new()
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = memory.Native,
                    Offset = flushOffset,
                    Size = flushEnd - flushOffset,
                };
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.FlushMappedMemoryRanges(
                        _device.Native,
                        1,
                        &range),
                    "vkFlushMappedMemoryRanges(parameter upload page)");
            }
            _offset = end;
            offset = start;
            return true;
        }

        internal void Reset() => _offset = 0;

        internal void Release()
        {
            VkBuffer native = _native;
            _native = default;
            if (native.Handle != 0)
                _device.Backend.Api.DestroyBuffer(_device.Native, native, null);
            _memory?.Release();
            _memory = null;
            _offset = 0;
        }
    }

    private sealed class VulkanUniformBuffer
    {
        private readonly VulkanDevice _device;
        private VulkanMemoryBlock? _memory;
        private VkBuffer _native;

        internal VulkanUniformBuffer(VulkanDevice device, ReadOnlySpan<byte> data)
        {
            _device = device;
            ulong size = Math.Max((ulong)data.Length, 16);
            BufferCreateInfo createInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.UniformBufferBit,
                SharingMode = SharingMode.Exclusive,
            };
            VkBuffer native = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateBuffer(device.Native, &createInfo, null, &native),
                "vkCreateBuffer(parameter uniform)");
            _native = native;
            try
            {
                Silk.NET.Vulkan.MemoryRequirements requirements;
                device.Backend.Api.GetBufferMemoryRequirements(device.Native, _native, &requirements);
                _memory = device.AllocateMemory(
                    requirements.Size,
                    requirements.MemoryTypeBits,
                    MemoryType.Upload,
                    deviceAddress: false);
                device.ThrowIfDeviceCallFailed(
                    device.Backend.Api.BindBufferMemory(device.Native, _native, _memory.Native, 0),
                    "vkBindBufferMemory(parameter uniform)");
                Write(data);
            }
            catch
            {
                Release();
                throw;
            }
        }

        internal VkBuffer Native => _native;

        internal void Write(ReadOnlySpan<byte> data)
        {
            VulkanMemoryBlock memory = _memory
                ?? throw new ObjectDisposedException(nameof(VulkanUniformBuffer));
            data.CopyTo(new Span<byte>((void*)memory.Mapped, data.Length));
            if (memory.Coherent || data.IsEmpty)
                return;
            ulong size = Math.Min(
                AlignUp(checked((ulong)data.Length), _device.NonCoherentAtomSize),
                memory.Size);
            MappedMemoryRange range = new()
            {
                SType = StructureType.MappedMemoryRange,
                Memory = memory.Native,
                Offset = 0,
                Size = size,
            };
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.FlushMappedMemoryRanges(_device.Native, 1, &range),
                "vkFlushMappedMemoryRanges(parameter uniform)");
        }

        internal void Release()
        {
            VkBuffer native = _native;
            _native = default;
            if (native.Handle != 0)
                _device.Backend.Api.DestroyBuffer(_device.Native, native, null);
            _memory?.Release();
            _memory = null;
        }
    }

    private sealed class VulkanDescriptorGeneration : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanDescriptorSetAllocation[] _allocations;
        private readonly VulkanUniformBuffer? _uniform;
        private readonly IVulkanRetained[] _retained;
        private readonly VulkanLifetime _lifetime;

        internal VulkanDescriptorGeneration(
            VulkanDevice device,
            VulkanDescriptorSetAllocation[] allocations,
            VulkanBoundDescriptorSet[] sets,
            VulkanUniformBuffer? uniform,
            IVulkanRetained[] retained,
            byte[] pushConstants,
            VulkanBlockLayout block)
        {
            _device = device;
            _allocations = allocations;
            Sets = sets;
            _uniform = uniform;
            _retained = retained;
            PushConstants = pushConstants;
            Block = block;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VulkanBoundDescriptorSet[] Sets { get; }
        internal byte[] PushConstants { get; }
        internal VulkanBlockLayout Block { get; }
        internal void CaptureSparseBindings(VulkanCommandSlot commands)
        {
            foreach (IVulkanRetained resource in _retained)
                commands.CaptureSparseBindings(resource);
        }

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        private void DestroyNative()
        {
            for (int index = _retained.Length - 1; index >= 0; index--)
                _retained[index].ReleaseNative();
            _uniform?.Release();
            foreach (VulkanDescriptorSetAllocation allocation in _allocations)
                _device.DescriptorAllocator.Free(allocation);
        }
    }

    private sealed class VulkanPersistentParameterBindings : PersistentParameterBindings
    {
        private readonly VulkanDevice _device;
        private VulkanDescriptorGeneration? _generation;
        private int _released;

        internal VulkanPersistentParameterBindings(
            VulkanDevice device,
            VulkanPipeline pipeline,
            VulkanBlockLayout block,
            VulkanDescriptorGeneration generation,
            string? label)
            : base(device, block.ReflectedLayout, label)
        {
            _device = device;
            Pipeline = pipeline;
            Block = block;
            _generation = generation;
        }

        internal VulkanPipeline Pipeline { get; }
        internal VulkanBlockLayout Block { get; }
        internal VulkanDescriptorGeneration? CurrentGeneration => Volatile.Read(ref _generation);

        internal VulkanDescriptorGeneration AcquireGeneration()
        {
            while (true)
            {
                VulkanDescriptorGeneration generation = Volatile.Read(ref _generation)
                    ?? throw new ObjectDisposedException(nameof(VulkanPersistentParameterBindings));
                try
                {
                    generation.RetainNative();
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }
                if (ReferenceEquals(generation, Volatile.Read(ref _generation)))
                    return generation;
                generation.ReleaseNative();
            }
        }

        internal void Publish(VulkanDescriptorGeneration generation)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            VulkanDescriptorGeneration? previous = Interlocked.Exchange(ref _generation, generation);
            previous?.ReleaseNative();
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Interlocked.Exchange(ref _generation, null)?.ReleaseNative();
            Pipeline.ReleaseNative();
            _device.UnregisterChild(this);
        }
    }

    private sealed class VulkanDescriptorTable : DescriptorTable
    {
        private readonly VulkanDevice _device;
        private readonly VulkanDescriptorRange _range;
        private readonly ResourceBinding[] _values;

        internal VulkanDescriptorTable(
            VulkanDevice device,
            DescriptorTableType type,
            in VulkanDescriptorRange range,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            string? label)
            : base(device, type, 0, slots, label)
        {
            _device = device;
            _range = range;
            _values = new ResourceBinding[slots.Length];
            for (int index = 0; index < slots.Length; index++)
                if (slots[index].Type != ResourceBindingType.Sampler)
                    _values[index] = ResourceBinding.Null(slots[index].Type);
        }

        internal uint FirstIndex => _range.First;
        internal VulkanDescriptorRange Range => _range;

        internal void Write(uint slot, in ResourceBinding value)
        {
            if (slot >= Count)
                throw new ArgumentOutOfRangeException(nameof(slot));
            ValidateTableDescriptor(
                _device,
                GetSlotDesc(slot),
                value);
            _device.BindlessPublisher.Write(this, slot, value);
        }

        internal ResourceBinding GetStagedValue(uint slot) =>
            _values[checked((int)slot)];

        internal ResourceBinding ExchangeStagedValue(
            uint slot,
            in ResourceBinding value)
        {
            ref ResourceBinding target = ref _values[checked((int)slot)];
            ResourceBinding previous = target;
            target = value;
            return previous;
        }

        internal ResourceBinding[] ClearStagedValues()
        {
            ResourceBinding[] values = _values.ToArray();
            Array.Clear(_values);
            return values;
        }

        internal override void Release(bool fromParent)
        {
            _device.BindlessPublisher.Unregister(this);
            _device.UnregisterChild(this);
        }
    }

    private static void ValidateTableDescriptor(
        VulkanDevice device,
        in DescriptorSlotDesc shape,
        in ResourceBinding value)
    {
        if (value.Type != shape.Type)
            throw new ArgumentException($"Descriptor slot requires {shape.Type}.", nameof(value));
        if (value.IsNull)
        {
            if (shape.Type == ResourceBindingType.Sampler)
                throw new ArgumentException("A Sampler descriptor cannot be null.", nameof(value));
            if (!device.ExtendedFeatures.NullDescriptor)
            {
                throw new NotSupportedException(
                    "This Vulkan Device cannot provide typed null descriptors.");
            }
            return;
        }
        value.Value!.ThrowIfDisposed();
        bool valid = value.Value switch
        {
            VulkanBuffer direct => shape.Type == ResourceBindingType.ConstantBuffer &&
                ReferenceEquals(direct.Device, device),
            VulkanBufferCbv cbv =>
                shape.Type == ResourceBindingType.ConstantBuffer &&
                ReferenceEquals(cbv.Device, device),
            VulkanBufferSrv srv =>
                shape.Type == ResourceBindingType.BufferSrv &&
                ReferenceEquals(srv.Device, device) &&
                srv.Description.Format == shape.Format &&
                srv.Description.StructureStride == shape.StructureStride,
            VulkanBufferUav uav =>
                shape.Type == ResourceBindingType.BufferUav &&
                ReferenceEquals(uav.Device, device) &&
                uav.Description.Format == shape.Format &&
                uav.Description.StructureStride == shape.StructureStride &&
                (uav.Description.CounterBuffer is not null) == shape.HasCounter,
            VulkanTextureSrv srv =>
                shape.Type == ResourceBindingType.TextureSrv &&
                ReferenceEquals(srv.Device, device) &&
                srv.Description.Format == shape.Format &&
                srv.Description.Dimension == shape.TextureDimension &&
                srv.Description.Range.Aspects == shape.Aspects,
            VulkanTextureUav uav =>
                shape.Type == ResourceBindingType.TextureUav &&
                ReferenceEquals(uav.Device, device) &&
                uav.Description.Format == shape.Format &&
                uav.Description.Dimension == shape.TextureDimension &&
                uav.Description.Range.Aspects == shape.Aspects,
            VulkanSampler sampler =>
                shape.Type == ResourceBindingType.Sampler &&
                ReferenceEquals(sampler.Device, device),
            VulkanAccelerationStructureSrv acceleration =>
                shape.Type == ResourceBindingType.AccelerationStructure &&
                ReferenceEquals(acceleration.Device, device),
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("The descriptor value does not match the Vulkan table slot shape.", nameof(value));
    }

    private static DescriptorType ToDescriptorType(
        ResourceBindingType type,
        in DescriptorSlotDesc shape) => type switch
        {
            ResourceBindingType.ConstantBuffer => DescriptorType.UniformBuffer,
            ResourceBindingType.BufferSrv => shape.Format.HasValue
                ? DescriptorType.UniformTexelBuffer
                : DescriptorType.StorageBuffer,
            ResourceBindingType.BufferUav => shape.Format.HasValue
                ? DescriptorType.StorageTexelBuffer
                : DescriptorType.StorageBuffer,
            ResourceBindingType.TextureSrv => DescriptorType.SampledImage,
            ResourceBindingType.TextureUav => DescriptorType.StorageImage,
            ResourceBindingType.Sampler => DescriptorType.Sampler,
            ResourceBindingType.AccelerationStructure =>
                DescriptorType.AccelerationStructureKhr,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
}
