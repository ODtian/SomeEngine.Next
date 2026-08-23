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
        try
        {
            var result = new VulkanPersistentParameterBindings(
                nativeDevice,
                nativePipeline,
                block,
                generation,
                label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            nativePipeline.ReleaseNative();
            generation.ReleaseNative();
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
        var table = VulkanDescriptorTable.Create(nativeDevice, type, slots, label);
        nativeDevice.RegisterChild(table);
        return table;
    }

    internal DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        VulkanDescriptorTable native = RequireDescriptorTable(table, nameof(table));
        if (slot >= native.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return new DescriptorIndex(native, slot);
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
        _ = RequireDevice(device, nameof(device));
        if (nodeIndex is not uint.MaxValue and not 0)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private VulkanDescriptorGeneration CreateDescriptorGeneration(
        VulkanDevice device,
        VulkanPipelineLayoutState pipelineLayout,
        VulkanBlockLayout block,
        in ParameterBlockBindings bindings)
    {
        VulkanDescriptorSlot[] slots = ValidateParameterBindings(block, bindings);
        var allocations = new VulkanDescriptorSetAllocation[block.SetIndices.Length];
        var sets = new Dictionary<uint, VkDescriptorSet>();
        VulkanUniformBuffer? uniform = null;
        var retained = new List<IVulkanRetained>();
        try
        {
            for (int index = 0; index < block.SetIndices.Length; index++)
            {
                uint set = block.SetIndices[index];
                allocations[index] = device.DescriptorAllocator.Allocate(
                    pipelineLayout.SetLayouts[checked((int)set)]);
                sets.Add(set, allocations[index].Set);
            }
            byte[] pushConstants = [];
            if (block.Ordinary is VulkanOrdinaryBinding ordinary)
            {
                if (ordinary.PushConstants)
                {
                    pushConstants = bindings.OrdinaryData.ToArray();
                }
                else
                {
                    uniform = new VulkanUniformBuffer(device, bindings.OrdinaryData);
                    DescriptorBufferInfo bufferInfo = new(uniform.Native, 0, ordinary.Size);
                    WriteNativeDescriptor(
                        device,
                        sets[ordinary.Set],
                        ordinary.Binding,
                        0,
                        DescriptorType.UniformBuffer,
                        &bufferInfo,
                        null,
                        null);
                }
            }
            for (int index = 0; index < slots.Length; index++)
            {
                VulkanDescriptorSlot slot = slots[index];
                ResourceBinding value = bindings.Resources[index];
                if (value.Value is IVulkanRetained dependency)
                {
                    dependency.RetainNative();
                    retained.Add(dependency);
                }
                WriteResourceDescriptor(device, sets[slot.Set], slot, value);
            }
            return new VulkanDescriptorGeneration(
                device,
                allocations,
                sets,
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

    private void WriteResourceDescriptor(
        VulkanDevice device,
        VkDescriptorSet set,
        in VulkanDescriptorSlot slot,
        in ResourceBinding value)
    {
        if (value.IsNull)
            return;
        DescriptorBufferInfo bufferInfo = default;
        DescriptorImageInfo imageInfo = default;
        VkBufferView texelView = default;
        switch (value.Value)
        {
            case VulkanAccelerationStructureSrv acceleration:
                WriteAccelerationStructureDescriptor(
                    device,
                    set,
                    slot.Binding,
                    slot.ArrayElement,
                    acceleration.Native);
                return;
            case VulkanBufferCbv cbv:
            {
                VulkanBuffer buffer = (VulkanBuffer)cbv.Resource;
                BufferRange range = cbv.Description.Range.Resolve(buffer.Info.Size);
                bufferInfo = new DescriptorBufferInfo(buffer.Native, range.Offset, range.Size);
                break;
            }
            case VulkanBufferSrv srv:
                PrepareBufferDescriptor(srv.Resource, srv.Description.Range, srv.Native, slot.DescriptorType, ref bufferInfo, ref texelView);
                break;
            case VulkanBufferUav uav:
                PrepareBufferDescriptor(uav.Resource, uav.Description.Range, uav.Native, slot.DescriptorType, ref bufferInfo, ref texelView);
                break;
            case VulkanTextureSrv srv:
                imageInfo = new DescriptorImageInfo(default, srv.Native, ImageLayout.ShaderReadOnlyOptimal);
                break;
            case VulkanTextureUav uav:
                imageInfo = new DescriptorImageInfo(default, uav.Native, ImageLayout.General);
                break;
            case VulkanSampler sampler:
                imageInfo = new DescriptorImageInfo(sampler.Native, default, ImageLayout.Undefined);
                break;
            default:
                throw new NotSupportedException($"Vulkan descriptor value {value.Value?.GetType().Name} is not implemented.");
        }
        WriteNativeDescriptor(
            device,
            set,
            slot.Binding,
            slot.ArrayElement,
            slot.DescriptorType,
            &bufferInfo,
            &imageInfo,
            &texelView);
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
        VulkanBlockLayout block,
        in ParameterBlockBindings bindings)
    {
        if (bindings.Layout != block.ReflectedLayout)
            throw new ArgumentException("The Slang parameter layout does not match the Pipeline block.", nameof(bindings));
        uint ordinarySize = block.Ordinary?.Size ?? 0;
        if (bindings.OrdinaryData.Length != ordinarySize)
            throw new ArgumentException($"The parameter block requires {ordinarySize} ordinary-data bytes.", nameof(bindings));
        return block.ResolveSlots(bindings.Resources);
    }

    private static void WriteNativeDescriptor(
        VulkanDevice device,
        VkDescriptorSet set,
        uint binding,
        uint arrayElement,
        DescriptorType type,
        DescriptorBufferInfo* buffer,
        DescriptorImageInfo* image,
        VkBufferView* texel)
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
                ? buffer
                : null,
            PImageInfo = type is DescriptorType.SampledImage or DescriptorType.StorageImage or
                DescriptorType.Sampler or DescriptorType.InputAttachment or DescriptorType.CombinedImageSampler
                ? image
                : null,
            PTexelBufferView = type is DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                ? texel
                : null,
        };
        device.Backend.Api.UpdateDescriptorSets(device.Native, 1, &write, 0, null);
    }

    private static void WriteAccelerationStructureDescriptor(
        VulkanDevice device,
        VkDescriptorSet set,
        uint binding,
        uint arrayElement,
        VkAccelerationStructure accelerationStructure)
    {
        WriteDescriptorSetAccelerationStructureKHR acceleration = new()
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &accelerationStructure,
        };
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &acceleration,
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = arrayElement,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        device.Backend.Api.UpdateDescriptorSets(device.Native, 1, &write, 0, null);
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
                    _active = CreatePool();
                    result = AllocateFrom(_active, layout, out set);
                }
                ThrowIfFailed(result, "vkAllocateDescriptorSets");
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
                ThrowIfFailed(result, "vkFreeDescriptorSets");
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
            ThrowIfFailed(
                _device.Backend.Api.CreateDescriptorPool(_device.Native, &createInfo, null, &pool),
                "vkCreateDescriptorPool");
            _pools.Add(pool);
            return pool;
        }
    }

    private readonly record struct VulkanDescriptorSetAllocation(
        VkDescriptorPool Pool,
        VkDescriptorSet Set);

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
            ThrowIfFailed(
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
                ThrowIfFailed(
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
            ThrowIfFailed(
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
            Dictionary<uint, VkDescriptorSet> sets,
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

        internal IReadOnlyDictionary<uint, VkDescriptorSet> Sets { get; }
        internal byte[] PushConstants { get; }
        internal VulkanBlockLayout Block { get; }
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
        private readonly ResourceBinding[] _values;
        private VkDescriptorSetLayout _layout;
        private VkDescriptorPool _pool;
        private VkDescriptorSet _set;

        private VulkanDescriptorTable(
            VulkanDevice device,
            DescriptorTableType type,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            string? label)
            : base(device, type, 0, slots, label)
        {
            _device = device;
            _values = new ResourceBinding[slots.Length];
        }

        internal static VulkanDescriptorTable Create(
            VulkanDevice device,
            DescriptorTableType type,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            string? label)
        {
            var result = new VulkanDescriptorTable(device, type, slots, label);
            result.CreateNative();
            return result;
        }

        internal void Write(uint slot, in ResourceBinding value)
        {
            if (slot >= Count)
                throw new ArgumentOutOfRangeException(nameof(slot));
            DescriptorSlotDesc shape = GetSlotDesc(slot);
            if (value.Type != shape.Type)
                throw new ArgumentException($"Descriptor slot {slot} requires {shape.Type}.", nameof(value));
            ResourceBinding previous = _values[slot];
            if (value.Value is IVulkanRetained next)
                next.RetainNative();
            try
            {
                VulkanDescriptorSlot nativeSlot = new(
                    value.Type,
                    ToDescriptorType(value.Type, shape),
                    0,
                    BindingFor(value.Type),
                    slot,
                    shape,
                    null);
                _device.Backend.WriteResourceDescriptor(_device, _set, nativeSlot, value);
                _values[slot] = value;
            }
            catch
            {
                if (value.Value is IVulkanRetained failedValue)
                    failedValue.ReleaseNative();
                throw;
            }
            if (previous.Value is IVulkanRetained old)
                old.ReleaseNative();
        }

        internal override void Release(bool fromParent)
        {
            for (int index = _values.Length - 1; index >= 0; index--)
                if (_values[index].Value is IVulkanRetained value)
                    value.ReleaseNative();
            if (_pool.Handle != 0)
                _device.Backend.Api.DestroyDescriptorPool(_device.Native, _pool, null);
            if (_layout.Handle != 0)
                _device.Backend.Api.DestroyDescriptorSetLayout(_device.Native, _layout, null);
            _set = default;
            _pool = default;
            _layout = default;
            _device.UnregisterChild(this);
        }

        private void CreateNative()
        {
            ResourceBindingType[] types = Slots
                .ToArray()
                .Select(static slot => slot.Type)
                .Distinct()
                .ToArray();
            DescriptorSetLayoutBinding[] bindings = new DescriptorSetLayoutBinding[types.Length];
            DescriptorPoolSize[] sizes = new DescriptorPoolSize[types.Length];
            for (int index = 0; index < types.Length; index++)
            {
                DescriptorSlotDesc representative = Slots.ToArray().First(slot => slot.Type == types[index]);
                DescriptorType descriptorType = ToDescriptorType(types[index], representative);
                bindings[index] = new DescriptorSetLayoutBinding(
                    BindingFor(types[index]),
                    descriptorType,
                    Count,
                    ShaderStageFlags.All,
                    null);
                sizes[index] = new DescriptorPoolSize(descriptorType, Count);
            }
            fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
            fixed (DescriptorPoolSize* sizePointer = sizes)
            {
                DescriptorSetLayoutCreateInfo layoutInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = checked((uint)bindings.Length),
                    PBindings = bindingPointer,
                };
                VkDescriptorSetLayout layout = default;
                ThrowIfFailed(
                    _device.Backend.Api.CreateDescriptorSetLayout(_device.Native, &layoutInfo, null, &layout),
                    "vkCreateDescriptorSetLayout(bindless table)");
                _layout = layout;
                DescriptorPoolCreateInfo poolInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = checked((uint)sizes.Length),
                    PPoolSizes = sizePointer,
                };
                VkDescriptorPool pool = default;
                ThrowIfFailed(
                    _device.Backend.Api.CreateDescriptorPool(_device.Native, &poolInfo, null, &pool),
                    "vkCreateDescriptorPool(bindless table)");
                _pool = pool;
                DescriptorSetAllocateInfo allocateInfo = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _pool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                VkDescriptorSet set = default;
                ThrowIfFailed(
                    _device.Backend.Api.AllocateDescriptorSets(_device.Native, &allocateInfo, &set),
                    "vkAllocateDescriptorSets(bindless table)");
                _set = set;
            }
        }

        private static uint BindingFor(ResourceBindingType type) => type switch
        {
            ResourceBindingType.ConstantBuffer => 0,
            ResourceBindingType.BufferSrv => 1,
            ResourceBindingType.BufferUav => 2,
            ResourceBindingType.TextureSrv => 3,
            ResourceBindingType.TextureUav => 4,
            ResourceBindingType.Sampler => 5,
            ResourceBindingType.AccelerationStructure => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

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
            ResourceBindingType.AccelerationStructure => DescriptorType.AccelerationStructureKhr,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
