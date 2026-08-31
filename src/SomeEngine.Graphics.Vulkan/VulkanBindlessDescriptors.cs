namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private enum VulkanBindlessKind : byte
    {
        ConstantBuffer,
        ReadOnlyStorageBuffer,
        WritableStorageBuffer,
        ReadOnlyTexelBuffer,
        WritableTexelBuffer,
        SampledImage,
        StorageImage,
        Sampler,
        AccelerationStructure,
    }

    private readonly record struct VulkanBindlessSetBinding(
        uint Set,
        VulkanBindlessKind Kind);

    private readonly record struct VulkanDescriptorRange(uint First, uint Count);

    private sealed class VulkanBindlessPublisher
    {
        private readonly VulkanDevice _device;
        private readonly object _gate = new();
        private readonly Dictionary<VulkanBindlessKind, VulkanBindlessHeap> _heaps = [];
        private readonly HashSet<VulkanDescriptorTable> _tables = [];
        private readonly HashSet<VulkanBindlessKind> _dirty = [];
        private readonly VulkanDescriptorIndexAllocator _resources;
        private readonly VulkanDescriptorIndexAllocator _samplers;
        private bool _released;

        internal VulkanBindlessPublisher(VulkanDevice device)
        {
            _device = device;
            _resources = new VulkanDescriptorIndexAllocator(
                device.Capabilities.Limits.ResourceDescriptorCapacity);
            _samplers = new VulkanDescriptorIndexAllocator(
                device.Capabilities.Limits.SamplerDescriptorCapacity);
        }

        internal VulkanDescriptorRange Reserve(DescriptorTableType type, uint count)
        {
            lock (_gate)
            {
                ThrowIfReleased();
                return Allocator(type).Reserve(count);
            }
        }

        internal void CancelReservation(
            DescriptorTableType type,
            in VulkanDescriptorRange range)
        {
            lock (_gate)
                Allocator(type).Free(range);
        }

        internal void Register(VulkanDescriptorTable table)
        {
            lock (_gate)
            {
                ThrowIfReleased();
                if (!_tables.Add(table))
                    throw new InvalidOperationException("A Vulkan DescriptorTable was registered twice.");
                foreach (DescriptorSlotDesc slot in table.Slots)
                    _dirty.Add(ResolveKind(slot));
            }
        }

        internal void Unregister(VulkanDescriptorTable table)
        {
            ResourceBinding[] values;
            lock (_gate)
            {
                if (!_tables.Remove(table))
                    return;
                foreach (DescriptorSlotDesc slot in table.Slots)
                    _dirty.Add(ResolveKind(slot));
                Allocator(table.Type).Free(table.Range);
                values = table.ClearStagedValues();
            }
            ReleaseValues(values);
        }

        internal void Write(
            VulkanDescriptorTable table,
            uint slot,
            in ResourceBinding value)
        {
            IVulkanRetained? next = value.Value as IVulkanRetained;
            next?.RetainNative();
            ResourceBinding previous;
            try
            {
                lock (_gate)
                {
                    ThrowIfReleased();
                    if (!_tables.Contains(table))
                        throw new ObjectDisposedException(nameof(VulkanDescriptorTable));
                    DescriptorSlotDesc shape = table.GetSlotDesc(slot);
                    if (value.Type != shape.Type)
                    {
                        throw new ArgumentException(
                            $"Descriptor slot {slot} requires {shape.Type}.",
                            nameof(value));
                    }
                    previous = table.ExchangeStagedValue(slot, value);
                    _dirty.Add(ResolveKind(shape));
                }
            }
            catch
            {
                next?.ReleaseNative();
                throw;
            }
            if (previous.Value is IVulkanRetained old)
                old.ReleaseNative();
        }

        internal void Publish(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfReleased();
                if (_dirty.Count == 0)
                    return;
                ValidateTablesForPublish();
                var replacements = new List<(VulkanBindlessHeap Heap,
                    VulkanBindlessGeneration Generation)>();
                try
                {
                    foreach (VulkanBindlessKind kind in _dirty.Order())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        VulkanBindlessHeap heap = GetOrCreateHeap(kind);
                        VulkanBindlessGeneration generation = heap.CreateGeneration();
                        replacements.Add((heap, generation));
                        PopulateGeneration(kind, generation);
                    }
                    foreach ((VulkanBindlessHeap heap,
                                 VulkanBindlessGeneration generation) in replacements)
                        heap.Publish(generation);
                    replacements.Clear();
                    _dirty.Clear();
                }
                finally
                {
                    foreach ((_, VulkanBindlessGeneration generation) in replacements)
                        generation.ReleaseNative();
                }
            }
        }

        internal VkDescriptorSetLayout GetLayout(VulkanBindlessKind kind)
        {
            lock (_gate)
            {
                ThrowIfReleased();
                return GetOrCreateHeap(kind).Layout;
            }
        }

        internal VulkanBindlessSnapshot Acquire(
            ReadOnlySpan<VulkanBindlessSetBinding> bindings)
        {
            lock (_gate)
            {
                ThrowIfReleased();
                var generations = new VulkanBindlessGeneration[bindings.Length];
                var sets = new VulkanBoundDescriptorSet[bindings.Length];
                int acquired = 0;
                try
                {
                    for (int index = 0; index < bindings.Length; index++)
                    {
                        VulkanBindlessHeap heap = GetOrCreateHeap(bindings[index].Kind);
                        VulkanBindlessGeneration generation = heap.AcquireGeneration();
                        generations[index] = generation;
                        sets[index] = new VulkanBoundDescriptorSet(
                            bindings[index].Set,
                            generation.Set);
                        acquired++;
                    }
                    return new VulkanBindlessSnapshot(generations, sets);
                }
                catch
                {
                    for (int index = acquired - 1; index >= 0; index--)
                        generations[index].ReleaseNative();
                    throw;
                }
            }
        }

        internal void Release()
        {
            VulkanDescriptorTable[] tables;
            lock (_gate)
            {
                if (_released)
                    return;
                _released = true;
                tables = _tables.ToArray();
                _tables.Clear();
            }
            foreach (VulkanDescriptorTable table in tables)
                ReleaseValues(table.ClearStagedValues());
            lock (_gate)
            {
                foreach (VulkanBindlessHeap heap in _heaps.Values)
                    heap.Release();
                _heaps.Clear();
                _dirty.Clear();
            }
        }

        internal static VulkanBindlessKind ResolveKind(
            ResourceBindingType bindingType,
            DescriptorType descriptorType) => (bindingType, descriptorType) switch
            {
                (ResourceBindingType.ConstantBuffer, DescriptorType.UniformBuffer) =>
                    VulkanBindlessKind.ConstantBuffer,
                (ResourceBindingType.BufferSrv, DescriptorType.StorageBuffer) =>
                    VulkanBindlessKind.ReadOnlyStorageBuffer,
                (ResourceBindingType.BufferUav, DescriptorType.StorageBuffer) =>
                    VulkanBindlessKind.WritableStorageBuffer,
                (ResourceBindingType.BufferSrv, DescriptorType.UniformTexelBuffer) =>
                    VulkanBindlessKind.ReadOnlyTexelBuffer,
                (ResourceBindingType.BufferUav, DescriptorType.StorageTexelBuffer) =>
                    VulkanBindlessKind.WritableTexelBuffer,
                (ResourceBindingType.TextureSrv, DescriptorType.SampledImage) =>
                    VulkanBindlessKind.SampledImage,
                (ResourceBindingType.TextureUav, DescriptorType.StorageImage) =>
                    VulkanBindlessKind.StorageImage,
                (ResourceBindingType.Sampler, DescriptorType.Sampler) =>
                    VulkanBindlessKind.Sampler,
                (ResourceBindingType.AccelerationStructure,
                    DescriptorType.AccelerationStructureKhr) =>
                    VulkanBindlessKind.AccelerationStructure,
                _ => throw new NotSupportedException(
                    $"Vulkan bindless descriptor {bindingType}/{descriptorType} is unsupported."),
            };

        internal static uint BindingFor(VulkanBindlessKind kind) => kind switch
        {
            VulkanBindlessKind.WritableStorageBuffer or
            VulkanBindlessKind.WritableTexelBuffer or
            VulkanBindlessKind.StorageImage => 0,
            VulkanBindlessKind.ReadOnlyStorageBuffer or
            VulkanBindlessKind.ReadOnlyTexelBuffer or
            VulkanBindlessKind.SampledImage or
            VulkanBindlessKind.AccelerationStructure => DescriptorRegisterClassStride,
            VulkanBindlessKind.Sampler => DescriptorRegisterClassStride * 2,
            VulkanBindlessKind.ConstantBuffer => DescriptorRegisterClassStride * 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private void PopulateGeneration(
            VulkanBindlessKind kind,
            VulkanBindlessGeneration generation)
        {
            foreach (VulkanDescriptorTable table in _tables)
            {
                for (uint slot = 0; slot < table.Count; slot++)
                {
                    DescriptorSlotDesc shape = table.GetSlotDesc(slot);
                    if (ResolveKind(shape) != kind)
                        continue;
                    ResourceBinding value = table.GetStagedValue(slot);
                    if (value.IsNull)
                        continue;
                    generation.Write(
                        table.FirstIndex + slot,
                        shape,
                        value,
                        kind);
                }
            }
        }

        private void ValidateTablesForPublish()
        {
            foreach (VulkanDescriptorTable table in _tables)
            {
                for (uint slot = 0; slot < table.Count; slot++)
                {
                    DescriptorSlotDesc shape = table.GetSlotDesc(slot);
                    ResourceBinding value = table.GetStagedValue(slot);
                    if (value.Type != shape.Type ||
                        shape.Type == ResourceBindingType.Sampler && value.IsNull)
                    {
                        throw new InvalidOperationException(
                            "Every Vulkan DescriptorTable slot must contain one typed value before publish.");
                    }
                }
            }
        }

        private VulkanDescriptorIndexAllocator Allocator(DescriptorTableType type) =>
            type == DescriptorTableType.Sampler ? _samplers : _resources;

        private VulkanBindlessHeap GetOrCreateHeap(VulkanBindlessKind kind)
        {
            if (_heaps.TryGetValue(kind, out VulkanBindlessHeap? heap))
                return heap;
            if (kind == VulkanBindlessKind.AccelerationStructure &&
                !_device.ExtendedFeatures.DescriptorBindingAccelerationStructureUpdateAfterBind)
            {
                throw new NotSupportedException(
                    "Bindless acceleration structures require descriptor update-after-bind support.");
            }
            heap = VulkanBindlessHeap.Create(
                _device,
                DescriptorTypeFor(kind),
                BindingFor(kind),
                kind == VulkanBindlessKind.Sampler
                    ? _device.Capabilities.Limits.SamplerDescriptorCapacity
                    : _device.Capabilities.Limits.ResourceDescriptorCapacity);
            _heaps.Add(kind, heap);
            return heap;
        }

        private static VulkanBindlessKind ResolveKind(in DescriptorSlotDesc shape) =>
            ResolveKind(shape.Type, ToDescriptorType(shape.Type, shape));

        private static DescriptorType DescriptorTypeFor(VulkanBindlessKind kind) => kind switch
        {
            VulkanBindlessKind.ConstantBuffer => DescriptorType.UniformBuffer,
            VulkanBindlessKind.ReadOnlyStorageBuffer or
            VulkanBindlessKind.WritableStorageBuffer => DescriptorType.StorageBuffer,
            VulkanBindlessKind.ReadOnlyTexelBuffer => DescriptorType.UniformTexelBuffer,
            VulkanBindlessKind.WritableTexelBuffer => DescriptorType.StorageTexelBuffer,
            VulkanBindlessKind.SampledImage => DescriptorType.SampledImage,
            VulkanBindlessKind.StorageImage => DescriptorType.StorageImage,
            VulkanBindlessKind.Sampler => DescriptorType.Sampler,
            VulkanBindlessKind.AccelerationStructure =>
                DescriptorType.AccelerationStructureKhr,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private static void ReleaseValues(ResourceBinding[] values)
        {
            for (int index = values.Length - 1; index >= 0; index--)
                if (values[index].Value is IVulkanRetained value)
                    value.ReleaseNative();
        }

        private void ThrowIfReleased() =>
            ObjectDisposedException.ThrowIf(_released, this);
    }

    private sealed class VulkanBindlessHeap
    {
        private readonly VulkanDevice _device;
        private readonly DescriptorType _type;
        private readonly uint _capacity;
        private VulkanBindlessGeneration? _current;

        private VulkanBindlessHeap(
            VulkanDevice device,
            DescriptorType type,
            uint capacity,
            VkDescriptorSetLayout layout,
            VulkanBindlessGeneration generation)
        {
            _device = device;
            _type = type;
            _capacity = capacity;
            Layout = layout;
            _current = generation;
        }

        internal VkDescriptorSetLayout Layout { get; private set; }

        internal static VulkanBindlessHeap Create(
            VulkanDevice device,
            DescriptorType type,
            uint binding,
            uint capacity)
        {
            if (capacity == 0)
                throw new NotSupportedException("The Vulkan Device exposes no bindless descriptor capacity.");
            DescriptorSetLayoutBinding nativeBinding = new(
                binding,
                type,
                capacity,
                ShaderStageFlags.All,
                null);
            DescriptorBindingFlags nativeFlags =
                DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.UpdateAfterBindBit |
                DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
            DescriptorSetLayoutBindingFlagsCreateInfo bindingFlags = new()
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &nativeFlags,
            };
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = 1,
                PBindings = &nativeBinding,
            };
            VkDescriptorSetLayout layout = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateDescriptorSetLayout(
                    device.Native,
                    &layoutInfo,
                    null,
                    &layout),
                "vkCreateDescriptorSetLayout(bindless publisher)");
            VulkanBindlessGeneration? generation = null;
            try
            {
                generation = VulkanBindlessGeneration.Create(
                    device,
                    layout,
                    type,
                    capacity);
                return new VulkanBindlessHeap(
                    device,
                    type,
                    capacity,
                    layout,
                    generation);
            }
            catch
            {
                generation?.ReleaseNative();
                device.Backend.Api.DestroyDescriptorSetLayout(device.Native, layout, null);
                throw;
            }
        }

        internal VulkanBindlessGeneration CreateGeneration() =>
            VulkanBindlessGeneration.Create(_device, Layout, _type, _capacity);

        internal VulkanBindlessGeneration AcquireGeneration()
        {
            while (true)
            {
                VulkanBindlessGeneration generation = Volatile.Read(ref _current)
                    ?? throw new ObjectDisposedException(nameof(VulkanBindlessHeap));
                try { generation.RetainNative(); }
                catch (ObjectDisposedException) { continue; }
                if (ReferenceEquals(generation, Volatile.Read(ref _current)))
                    return generation;
                generation.ReleaseNative();
            }
        }

        internal void Publish(VulkanBindlessGeneration generation)
        {
            VulkanBindlessGeneration? previous = Interlocked.Exchange(
                ref _current,
                generation);
            previous?.ReleaseNative();
        }

        internal void Release()
        {
            Interlocked.Exchange(ref _current, null)?.ReleaseNative();
            if (Layout.Handle != 0)
                _device.Backend.Api.DestroyDescriptorSetLayout(
                    _device.Native,
                    Layout,
                    null);
            Layout = default;
        }
    }

    private sealed class VulkanBindlessGeneration : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly List<IVulkanRetained> _retained = [];
        private readonly VulkanLifetime _lifetime;
        private VkDescriptorPool _pool;

        private VulkanBindlessGeneration(
            VulkanDevice device,
            VkDescriptorPool pool,
            VkDescriptorSet set)
        {
            _device = device;
            _pool = pool;
            Set = set;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkDescriptorSet Set { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal static VulkanBindlessGeneration Create(
            VulkanDevice device,
            VkDescriptorSetLayout layout,
            DescriptorType type,
            uint capacity)
        {
            DescriptorPoolSize size = new(type, capacity);
            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &size,
            };
            VkDescriptorPool pool = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateDescriptorPool(
                    device.Native,
                    &poolInfo,
                    null,
                    &pool),
                "vkCreateDescriptorPool(bindless generation)");
            try
            {
                DescriptorSetAllocateInfo allocation = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = pool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                VkDescriptorSet set = default;
                device.ThrowIfDeviceCallFailed(
                    device.Backend.Api.AllocateDescriptorSets(
                        device.Native,
                        &allocation,
                        &set),
                    "vkAllocateDescriptorSets(bindless generation)");
                return new VulkanBindlessGeneration(device, pool, set);
            }
            catch
            {
                device.Backend.Api.DestroyDescriptorPool(device.Native, pool, null);
                throw;
            }
        }

        internal void Write(
            uint index,
            in DescriptorSlotDesc shape,
            in ResourceBinding value,
            VulkanBindlessKind kind)
        {
            if (value.Value is IVulkanRetained retained)
            {
                retained.RetainNative();
                _retained.Add(retained);
            }
            var slot = new VulkanDescriptorSlot(
                value.Type,
                ToDescriptorType(value.Type, shape),
                0,
                VulkanBindlessPublisher.BindingFor(kind),
                index,
                shape,
                null);
            _device.Backend.WriteResourceDescriptor(
                _device,
                Set,
                slot,
                value);
        }

        private void DestroyNative()
        {
            if (_pool.Handle != 0)
                _device.Backend.Api.DestroyDescriptorPool(_device.Native, _pool, null);
            _pool = default;
            for (int index = _retained.Count - 1; index >= 0; index--)
                _retained[index].ReleaseNative();
            _retained.Clear();
        }
    }

    private sealed class VulkanBindlessSnapshot : IVulkanRetained
    {
        private readonly VulkanBindlessGeneration[] _generations;
        private readonly VulkanLifetime _lifetime;

        internal VulkanBindlessSnapshot(
            VulkanBindlessGeneration[] generations,
            VulkanBoundDescriptorSet[] sets)
        {
            _generations = generations;
            Sets = sets;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VulkanBoundDescriptorSet[] Sets { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        private void DestroyNative()
        {
            for (int index = _generations.Length - 1; index >= 0; index--)
                _generations[index].ReleaseNative();
        }
    }

    private readonly record struct VulkanBoundDescriptorSet(
        uint Set,
        VkDescriptorSet Native);

    private sealed class VulkanDescriptorIndexAllocator(uint capacity)
    {
        private readonly List<VulkanDescriptorRange> _free = [];
        private uint _next;

        internal VulkanDescriptorRange Reserve(uint count)
        {
            if (count == 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            for (int index = 0; index < _free.Count; index++)
            {
                VulkanDescriptorRange candidate = _free[index];
                if (candidate.Count < count)
                    continue;
                var result = new VulkanDescriptorRange(candidate.First, count);
                if (candidate.Count == count)
                    _free.RemoveAt(index);
                else
                    _free[index] = new VulkanDescriptorRange(
                        checked(candidate.First + count),
                        candidate.Count - count);
                return result;
            }
            if (count > capacity - _next)
                throw new GraphicsException(
                    GraphicsError.OutOfMemory,
                    $"The Vulkan bindless descriptor publisher exhausted its {capacity} slots.");
            VulkanDescriptorRange allocated = new(_next, count);
            _next = checked(_next + count);
            return allocated;
        }

        internal void Free(in VulkanDescriptorRange range)
        {
            if (range.Count == 0 || range.First > capacity - range.Count)
                throw new ArgumentOutOfRangeException(nameof(range));
            int index = _free.BinarySearch(
                range,
                Comparer<VulkanDescriptorRange>.Create(
                    static (left, right) => left.First.CompareTo(right.First)));
            if (index >= 0)
                throw new InvalidOperationException("A Vulkan descriptor range was freed twice.");
            index = ~index;
            _free.Insert(index, range);
            if (index > 0 &&
                checked(_free[index - 1].First + _free[index - 1].Count) == _free[index].First)
            {
                _free[index - 1] = new VulkanDescriptorRange(
                    _free[index - 1].First,
                    checked(_free[index - 1].Count + _free[index].Count));
                _free.RemoveAt(index--);
            }
            if (index + 1 < _free.Count &&
                checked(_free[index].First + _free[index].Count) == _free[index + 1].First)
            {
                _free[index] = new VulkanDescriptorRange(
                    _free[index].First,
                    checked(_free[index].Count + _free[index + 1].Count));
                _free.RemoveAt(index + 1);
            }
        }
    }
}
