using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeHeapDesc = Silk.NET.Direct3D12.HeapDesc;
using NativeHeapFlags = Silk.NET.Direct3D12.HeapFlags;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;
/// <summary>Reports the current Direct3D 12 private resource-pool state.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable snapshots may be shared.</para>
/// <para><b>Ownership:</b> Pure value; owns no native objects.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; a previously captured data remains readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12MemoryAllocatorInfo(
    ulong PooledHeapBytes,
    ulong PooledAllocatedBytes,
    int HeapCount,
    int AllocationCount,
    ulong CommittedFallbackCount,
    ulong BudgetPressureFallbackCount
);
internal sealed unsafe partial class D3D12Backend
{
    private enum ResourceHeapClass : byte
    {
        Buffers,
        Textures,
        Attachments,
    }

    private readonly record struct ResourcePoolKey(MemoryType MemoryType, ResourceHeapClass Class, uint CreationNodeMask, uint VisibleNodeMask);
    private readonly record struct FreeResourceRange(ulong Offset, ulong Size)
    {
        internal ulong End => checked(Offset + Size);
    }

    private sealed class D3D12MemoryAllocation
    {
        private D3D12ResourceAllocator? _allocator;
        private int _references = 1;
        internal D3D12MemoryAllocation(D3D12ResourceAllocator allocator, ResourceHeapBlock block, ulong offset, ulong size)
        {
            _allocator = allocator;
            Block = block;
            Offset = offset;
            Size = size;
        }

        internal ResourceHeapBlock Block { get; }
        internal ulong Offset { get; }
        internal ulong Size { get; }
        internal ID3D12Heap* NativeHeap => Block.Native;
        internal NativeResourceData HeapLifetime => Block.NativeData;

        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            D3D12ResourceAllocator? allocator = Interlocked.Exchange(ref _allocator, null);
            allocator?.Return(this);
        }
    }

    private sealed class ResourceHeapBlock : IDisposable
    {
        private readonly NativeResourceData _native;
        private readonly List<FreeResourceRange> _freeRanges;
        private int _activeAllocations;
        private ResourceHeapBlock(NativeResourceData native, in ResourcePoolKey key, ulong size, List<FreeResourceRange> freeRanges)
        {
            _native = native;
            Key = key;
            Size = size;
            _freeRanges = freeRanges;
        }

        internal ResourcePoolKey Key { get; }
        internal ulong Size { get; }
        internal ulong AllocatedBytes { get; private set; }
        internal int ActiveAllocations => _activeAllocations;
        internal ID3D12Heap* Native => (ID3D12Heap*)_native.Pointer;
        internal NativeResourceData NativeData => _native;

        internal static ResourceHeapBlock Create(D3D12Device device, in ResourcePoolKey key, ulong size)
        {
            var freeRanges = new List<FreeResourceRange>(1)
            {
                new(0, size),
            };
            NativeHeapDesc description = new()
            {
                SizeInBytes = size,
                Properties = CreateHeapProperties(key.MemoryType, key.CreationNodeMask, key.VisibleNodeMask),
                Flags = key.Class switch
                {
                    ResourceHeapClass.Buffers => NativeHeapFlags.AllowOnlyBuffers,
                    ResourceHeapClass.Textures => NativeHeapFlags.AllowOnlyNonRTDSTextures,
                    ResourceHeapClass.Attachments => NativeHeapFlags.AllowOnlyRTDSTextures,
                    _ => default!,
                },
            };
            description.Flags |= NativeHeapFlags.CreateNotZeroed;
            ID3D12Heap* heap = null;
            NativeResourceData? lifetime = null;
            Guid iid = ID3D12Heap.Guid;
            try
            {
                ThrowIfFailed(
                    device,
                    DeviceResourceTrace.CreateHeap(device.Native, &description, &iid, (void**)&heap),
                    NativeOperationType.Ordinary,
                    $"ID3D12Device::CreateHeap(resource-pool size={size}, " + $"memory={key.MemoryType}, class={key.Class})"
                );
                SetNativeName(heap, $"{key.MemoryType} {key.Class} Resource Pool " + $"(size={size}, nodeMask=0x{key.CreationNodeMask:X})");
                lifetime = new NativeResourceData((IUnknown*)heap, ownsReference: true);
                heap = null;
                ResourceHeapBlock result = new(lifetime.Value, key, size, freeRanges);
                lifetime = null;
                return result;
            }
            finally
            {
                lifetime?.Release();
                if (heap is not null)
                    _ = ResourceTrace.Release(heap);
            }
        }

        internal bool TryAllocate(
            D3D12ResourceAllocator allocator,
            ulong size,
            ulong alignment,
            out D3D12MemoryAllocation? allocation
        )
        {
            if (!TryTakeByteRange(_freeRanges, size, alignment, out ulong offset))
            {
                allocation = null;
                return false;
            }

            try
            {
                allocation = new D3D12MemoryAllocation(allocator, this, offset, size);
            }
            catch
            {
                ReturnByteRange(_freeRanges, offset, size);
                throw;
            }

            _activeAllocations = checked(_activeAllocations + 1);
            AllocatedBytes = checked(AllocatedBytes + size);
            return true;
        }

        internal void Return(ulong offset, ulong size)
        {
            ReturnByteRange(_freeRanges, offset, size);
            _activeAllocations--;
            AllocatedBytes -= size;
        }

        public void Dispose() => _native.Release();
    }

    private static bool TryTakeByteRange(List<FreeResourceRange> ranges, ulong size, ulong alignment, out ulong offset)
    {
        for (int index = 0; index < ranges.Count; index++)
        {
            FreeResourceRange range = ranges[index];
            ulong aligned = AlignResourceOffset(range.Offset, alignment);
            if (aligned < range.Offset || aligned - range.Offset > range.Size)
                continue;
            ulong prefix = aligned - range.Offset;
            if (size > range.Size - prefix)
                continue;
            ulong end = checked(aligned + size);
            ulong suffix = range.End - end;
            ranges.EnsureCapacity(checked(ranges.Count + 1));
            if (prefix != 0 && suffix != 0)
            {
                ranges[index] = new(range.Offset, prefix);
                ranges.Insert(index + 1, new(end, suffix));
            }
            else if (prefix != 0)
                ranges[index] = new(range.Offset, prefix);
            else if (suffix != 0)
                ranges[index] = new(end, suffix);
            else
                ranges.RemoveAt(index);
            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }

    private static void ReturnByteRange(List<FreeResourceRange> ranges, ulong offset, ulong size)
    {
        int index = 0;
        while (index < ranges.Count && ranges[index].Offset < offset)
            index++;
        ranges.Insert(index, new(offset, size));
        if (index > 0 && ranges[index - 1].End == ranges[index].Offset)
        {
            FreeResourceRange previous = ranges[index - 1], current = ranges[index];
            ranges[index - 1] = new(previous.Offset, checked(previous.Size + current.Size));
            ranges.RemoveAt(index);
            index--;
        }

        if (index + 1 < ranges.Count && ranges[index].End == ranges[index + 1].Offset)
        {
            FreeResourceRange current = ranges[index], next = ranges[index + 1];
            ranges[index] = new(current.Offset, checked(current.Size + next.Size));
            ranges.RemoveAt(index + 1);
        }
    }

    private sealed class ParameterBytePage(D3D12OrdinaryDataChunk bytes)
    {
        private int _references = 1;
        internal D3D12OrdinaryDataChunk Bytes { get; } = bytes;
        internal List<FreeResourceRange> FreeRanges { get; } = [new(0, bytes.Capacity)];
        internal bool HasParameters => Volatile.Read(ref _references) > 1;

        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                Bytes.Release();
        }
    }

    private sealed class D3D12ParameterBytes(D3D12ResourceAllocator allocator, ParameterBytePage page, ulong offset, ulong size)
    {
        private D3D12ResourceAllocator? _allocator = allocator;
        internal ParameterBytePage Page { get; } = page;
        internal ulong Offset { get; } = offset;
        internal ulong Size { get; } = size;
        internal ulong Address => checked(Page.Bytes.GpuAddress + Offset);

        internal void Release() => Interlocked.Exchange(ref _allocator, null)?.ReturnParameterBytes(this);
    }

    private sealed class D3D12ResourceAllocator : IDisposable
    {
        private const ulong DeviceLocalBlockSize = 64UL * 1024 * 1024;
        private const ulong HostVisibleBlockSize = 16UL * 1024 * 1024;
        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private readonly Dictionary<ResourcePoolKey, List<ResourceHeapBlock>> _blocks = [];
        private readonly List<ParameterBytePage> _parameterBytes = [];
        private ulong _committedFallbackCount;
        private ulong _budgetPressureFallbackCount;
        private bool _disposed;
        internal D3D12ResourceAllocator(D3D12Device device) => _device = device;
        internal D3D12ParameterBytes AllocateParameterBytes(ReadOnlySpan<byte> data)
        {
            ulong size = AlignResourceOffset((ulong)data.Length, 256);
            lock (_gate)
            {
                foreach (ParameterBytePage page in _parameterBytes)
                {
                    if (TryTakeByteRange(page.FreeRanges, size, 256, out ulong offset))
                        return WriteParameterBytes(page, offset, size, data);
                }

                _parameterBytes.EnsureCapacity(checked(_parameterBytes.Count + 1));
                D3D12OrdinaryDataChunk bytes = D3D12OrdinaryDataChunk.Create(_device, _device.PrimaryNodeMask, Math.Max(1024UL * 1024, size), _device.EnabledNodeMask);
                ParameterBytePage created;
                try
                {
                    created = new(bytes);
                }
                catch
                {
                    bytes.Release();
                    throw;
                }

                _parameterBytes.Add(created);
                _ = TryTakeByteRange(created.FreeRanges, size, 256, out ulong firstOffset);
                return WriteParameterBytes(created, firstOffset, size, data);
            }
        }

        private D3D12ParameterBytes WriteParameterBytes(ParameterBytePage page, ulong offset, ulong size, ReadOnlySpan<byte> data)
        {
            D3D12ParameterBytes result;
            try
            {
                result = new(this, page, offset, size);
                page.Bytes.WriteBytes(offset, data);
            }
            catch
            {
                ReturnByteRange(page.FreeRanges, offset, size);
                throw;
            }

            page.Retain();
            return result;
        }

        internal void ReturnParameterBytes(D3D12ParameterBytes data)
        {
            lock (_gate)
            {
                if (!_disposed)
                    ReturnByteRange(data.Page.FreeRanges, data.Offset, data.Size);
                data.Page.Release();
                if (!_disposed && _parameterBytes.Count > 1 && !data.Page.HasParameters)
                {
                    _parameterBytes.Remove(data.Page);
                    data.Page.Release();
                }
            }
        }

        internal D3D12MemoryAllocation? TryAllocate(
            MemoryType memoryType,
            ResourceHeapClass heapClass,
            uint creationNodeMask,
            uint visibleNodeMask,
            in MemoryRequirements requirements,
            bool poolEligible
        )
        {
            lock (_gate)
            {
                ulong blockSize = memoryType == MemoryType.DeviceLocal ? DeviceLocalBlockSize : HostVisibleBlockSize;
                if (!poolEligible || requirements.Size > blockSize / 2)
                {
                    _committedFallbackCount++;
                    return null;
                }

                ResourcePoolKey key = new(memoryType, heapClass, creationNodeMask, visibleNodeMask);
                if (_blocks.TryGetValue(key, out List<ResourceHeapBlock>? existing))
                {
                    foreach (ResourceHeapBlock existingBlock in existing)
                    {
                        if (existingBlock.TryAllocate(this, requirements.Size, requirements.Alignment, out D3D12MemoryAllocation? allocation))
                        {
                            return allocation;
                        }
                    }
                }

                if (memoryType == MemoryType.DeviceLocal && WouldExceedBudget(creationNodeMask, blockSize))
                {
                    _committedFallbackCount++;
                    _budgetPressureFallbackCount++;
                    return null;
                }

                bool addBlockList = existing is null;
                if (addBlockList)
                {
                    _blocks.EnsureCapacity(checked(_blocks.Count + 1));
                    existing = new List<ResourceHeapBlock>(1);
                }
                else
                {
                    existing!.EnsureCapacity(checked(existing.Count + 1));
                }

                var block = ResourceHeapBlock.Create(_device, key, blockSize);
                D3D12MemoryAllocation? seed = null;
                bool listAdded = false;
                try
                {
                    block.TryAllocate(this, requirements.Size, requirements.Alignment, out seed);
                    if (addBlockList)
                    {
                        _blocks.Add(key, existing!);
                        listAdded = true;
                    }

                    existing.Add(block);
                    return seed;
                }
                catch
                {
                    seed?.Release();
                    if (listAdded && existing!.Count == 0)
                        _blocks.Remove(key);
                    block.Dispose();
                    throw;
                }
            }
        }

        internal D3D12MemoryAllocatorInfo GetInfo()
        {
            lock (_gate)
            {
                ulong heapBytes = 0;
                ulong allocatedBytes = 0;
                int heapCount = 0;
                int allocationCount = 0;
                foreach (List<ResourceHeapBlock> blocks in _blocks.Values)
                    foreach (ResourceHeapBlock block in blocks)
                    {
                        heapBytes = checked(heapBytes + block.Size);
                        allocatedBytes = checked(allocatedBytes + block.AllocatedBytes);
                        heapCount++;
                        allocationCount = checked(allocationCount + block.ActiveAllocations);
                    }

                return new D3D12MemoryAllocatorInfo(heapBytes, allocatedBytes, heapCount, allocationCount, _committedFallbackCount, _budgetPressureFallbackCount);
            }
        }

        internal void Return(D3D12MemoryAllocation allocation)
        {
            lock (_gate)
            {
                allocation.Block.Return(allocation.Offset, allocation.Size);
                TrimEmptyBlocks(allocation.Block.Key);
            }
        }

        private void TrimEmptyBlocks(in ResourcePoolKey key)
        {
            if (!_blocks.TryGetValue(key, out List<ResourceHeapBlock>? blocks) || blocks.Count <= 1)
            {
                return;
            }

            for (int index = blocks.Count - 1; index > 0; index--)
            {
                if (blocks[index].ActiveAllocations != 0)
                    continue;
                ResourceHeapBlock block = blocks[index];
                blocks.RemoveAt(index);
                block.Dispose();
            }
        }

        private bool WouldExceedBudget(uint creationNodeMask, ulong additionalBytes)
        {
            uint nodeIndex = checked((uint)BitOperations.TrailingZeroCount(creationNodeMask));
            QueryVideoMemoryInfo info = default;
            int result = PresentationTrace.QueryVideoMemoryInfo(((IDXGIAdapter3*)_device.NativeAdapter), nodeIndex, MemorySegmentGroup.Local, &info);
            ThrowIfFailed(_device, result, NativeOperationType.Ordinary, "IDXGIAdapter3::QueryVideoMemoryInfo(resource-pool)");
            if (info.Budget == 0)
                return false;
            ulong headroom = info.CurrentUsage >= info.Budget ? 0 : info.Budget - info.CurrentUsage;
            ulong pressureLimit = info.Budget - info.Budget / 10;
            return additionalBytes > headroom || info.CurrentUsage >= pressureLimit || additionalBytes > pressureLimit - info.CurrentUsage;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (List<ResourceHeapBlock> blocks in _blocks.Values)
                    foreach (ResourceHeapBlock block in blocks)
                        block.Dispose();
                _blocks.Clear();
                foreach (ParameterBytePage page in _parameterBytes)
                    page.Release();
                _parameterBytes.Clear();
            }
        }
    }

    private static NativeResourceData? TryCreatePooledResource(
        D3D12Device device,
        MemoryType memoryType,
        ResourceHeapClass heapClass,
        bool poolEligible,
        uint creationNodeMask,
        uint visibleNodeMask,
        in MemoryRequirements requirements,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats
    )
    {
        D3D12MemoryAllocation? allocation = device.ResourceAllocator.TryAllocate(memoryType, heapClass, creationNodeMask, visibleNodeMask, requirements, poolEligible);
        if (allocation is null)
            return null;
        NativeResource* resource = null;
        try
        {
            resource = CreatePlacedResource(device, allocation.NativeHeap, allocation.Offset, memoryType, description, castableFormats);
            NativeResourceData lifetime = new((IUnknown*)resource, ownsReference: true, allocation: allocation);
            resource = null;
            allocation = null;
            return lifetime;
        }
        finally
        {
            if (resource is not null)
                _ = ResourceTrace.Release(resource);
            allocation?.Release();
        }
    }

    internal static D3D12MemoryAllocatorInfo GetMemoryAllocatorInfo(Device device) => ((D3D12Device)device).ResourceAllocator.GetInfo();
    private static D3D12MemoryAllocation? GetMemoryAllocationOrNull(Resource resource) => resource switch
    {
        D3D12Buffer buffer => buffer.MemoryAllocation,
        D3D12Texture texture => texture.NativeResource.MemoryAllocation,
        D3D12SamplerFeedbackTexture feedback => feedback.NativeResource.MemoryAllocation,
        _ => null,
    };
    private static ulong AlignResourceOffset(ulong value, ulong alignment)
    {
        return checked((value + alignment - 1) & ~(alignment - 1));
    }

    private sealed partial class D3D12Device
    {
        private readonly D3D12ResourceAllocator _resourceAllocator;
        internal D3D12ResourceAllocator ResourceAllocator => _resourceAllocator;
    }
}
