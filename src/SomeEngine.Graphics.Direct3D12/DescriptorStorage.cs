using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    private interface INativeDescriptor
    {
        DescriptorAllocation NativeDescriptor { get; }
    }

    private sealed class DescriptorAllocator : IDisposable
    {
        private readonly D3D12Device _device;
        private readonly DescriptorHeapType _type;
        private readonly uint _capacity;
        private readonly bool _shaderVisible;
        private readonly int _maximumHeapCount;
        private readonly uint _nodeMask;
        private readonly object _gate = new();
        private readonly List<DescriptorPage> _pages = [];
        private bool _disposed;
        internal DescriptorAllocator(
            D3D12Device device,
            DescriptorHeapType type,
            uint capacity,
            bool shaderVisible,
            int maximumHeapCount,
            uint nodeMask
        )
        {
            _device = device;
            _type = type;
            _capacity = capacity;
            _shaderVisible = shaderVisible;
            _maximumHeapCount = maximumHeapCount;
            _nodeMask = nodeMask;
        }

        internal DescriptorAllocation Copy(DescriptorAllocation source)
        {
            DescriptorAllocation result = Allocate();
            DeviceQueryTrace.CopyDescriptorsSimple(_device.Native, 1, result.Cpu, source.Cpu, _type);
            return result;
        }

        internal DescriptorAllocation Allocate()
        {
            lock (_gate)
            {
                foreach (DescriptorPage page in _pages)
                {
                    if (page.TryAllocate(out uint entry))
                    {
                        try
                        {
                            return new DescriptorAllocation(this, page, entry);
                        }
                        catch
                        {
                            page.Return(entry);
                            throw;
                        }
                    }
                }

                if (_pages.Count >= _maximumHeapCount)
                {
                    throw new GraphicsException(GraphicsError.OutOfMemory, $"The D3D12 {_type} descriptor capacity is exhausted.");
                }

                DescriptorPage newPage = new(_device, _type, _capacity, _shaderVisible, _nodeMask);
                bool added = false;
                try
                {
                    _pages.Add(newPage);
                    added = true;
                    newPage.TryAllocate(out uint newSlot);
                    try
                    {
                        return new DescriptorAllocation(this, newPage, newSlot);
                    }
                    catch
                    {
                        newPage.Return(newSlot);
                        throw;
                    }
                }
                catch
                {
                    if (added)
                        _pages.Remove(newPage);
                    newPage.Dispose();
                    throw;
                }
            }
        }

        internal void Return(DescriptorPage page, uint entry)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                page.Return(entry);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (DescriptorPage page in _pages)
                    page.Dispose();
                _pages.Clear();
            }
        }
    }

    private sealed class DescriptorPage : IDisposable
    {
        private const uint AllocatedEntry = uint.MaxValue;
        private const uint EndOfFreeList = uint.MaxValue - 1;
        private readonly uint[] _free;
        private readonly uint _capacity;
        private readonly uint _increment;
        private uint _freeHead = EndOfFreeList;
        private int _freeCount;
        private uint _nextIndex;
        private ID3D12DescriptorHeap* _heap;
        internal DescriptorPage(D3D12Device device, DescriptorHeapType type, uint capacity, bool shaderVisible, uint nodeMask)
        {
            _free = new uint[checked((int)capacity)];
            _capacity = capacity;
            _increment = device.DescriptorIncrement(type);
            DescriptorHeapDesc description = new(type, capacity, shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None, nodeMask);
            Guid iid = ID3D12DescriptorHeap.Guid;
            ID3D12DescriptorHeap* heap = null;
            ThrowIfFailed(
                device,
                DeviceResourceTrace.CreateDescriptorHeap(device.Native, &description, &iid, (void**)&heap),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap"
            );
            _heap = heap;
            SetNativeName(heap, $"{type} Descriptor Heap (capacity={capacity}, nodeMask=0x{nodeMask:X})");
            CpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_heap);
            GpuStart = shaderVisible ? ResourceTrace.GetGPUDescriptorHandleForHeapStart(_heap) : default;
        }

        internal ID3D12DescriptorHeap* Heap => _heap;
        internal CpuDescriptorHandle CpuStart { get; }
        internal GpuDescriptorHandle GpuStart { get; }

        internal bool TryAllocate(out uint entry)
        {
            if (_freeCount != 0)
            {
                entry = _freeHead;
                _freeHead = _free[(int)entry];
                _free[(int)entry] = AllocatedEntry;
                _freeCount--;
                return true;
            }

            if (_nextIndex >= _capacity)
            {
                entry = 0;
                return false;
            }

            entry = _nextIndex++;
            _free[(int)entry] = AllocatedEntry;
            return true;
        }

        internal void Return(uint entry)
        {
            if (entry >= _nextIndex)
                return;
            int index = (int)entry;
            if (_free[index] != AllocatedEntry || _freeCount >= _free.Length)
                return;
            _free[index] = _freeHead;
            _freeHead = entry;
            _freeCount++;
        }

        internal CpuDescriptorHandle GetCpu(uint entry) => new(CpuStart.Ptr + checked((nuint)(entry * _increment)));
        internal GpuDescriptorHandle GetGpu(uint entry) => new(GpuStart.Ptr + checked((ulong)entry * _increment));
        public void Dispose()
        {
            ID3D12DescriptorHeap* heap = _heap;
            _heap = null;
            if (heap is not null)
                _ = ResourceTrace.Release(heap);
        }
    }

    private sealed class DescriptorAllocation
    {
        private DescriptorAllocator? _allocator;
        private readonly DescriptorPage _page;
        private readonly uint _descriptorIndex;
        internal DescriptorAllocation(DescriptorAllocator allocator, DescriptorPage page, uint entry)
        {
            _allocator = allocator;
            _page = page;
            _descriptorIndex = entry;
        }

        internal CpuDescriptorHandle Cpu => _page.GetCpu(_descriptorIndex);
        internal GpuDescriptorHandle Gpu => _page.GetGpu(_descriptorIndex);
        internal ID3D12DescriptorHeap* Heap => _page.Heap;
        internal uint Index => _descriptorIndex;

        internal DescriptorAllocation Copy() => _allocator!.Copy(this);
        internal void Release()
        {
            DescriptorAllocator? allocator = Interlocked.Exchange(ref _allocator, null);
            allocator?.Return(_page, _descriptorIndex);
        }
    }

    private sealed class ViewReferences
    {
        private readonly D3D12Device _device;
        private readonly DescriptorAllocation _descriptor;
        private NativeResourceData? _resource;
        private NativeResourceData? _secondaryResource;
        internal ViewReferences(
            D3D12Device device,
            DescriptorAllocation descriptor,
            NativeResourceData? resource = null,
            NativeResourceData? secondaryResource = null
        )
        {
            _device = device;
            _descriptor = descriptor;
            resource?.Retain();
            try
            {
                secondaryResource?.Retain();
            }
            catch
            {
                resource?.Release();
                throw;
            }

            _resource = resource;
            _secondaryResource = secondaryResource;
        }

        internal void Release(GraphicsObject allocator)
        {
            _descriptor.Release();
            _secondaryResource?.Release();
            _secondaryResource = null;
            _resource?.Release();
            _resource = null;
            _device.UnregisterChild(allocator);
        }
    }
}
