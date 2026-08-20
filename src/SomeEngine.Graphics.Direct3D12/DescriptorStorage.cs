using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private interface INativeDescriptor
    {
        DescriptorLease NativeDescriptor { get; }
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
            uint nodeMask)
        {
            _device = device;
            _type = type;
            _capacity = capacity;
            _shaderVisible = shaderVisible;
            _maximumHeapCount = maximumHeapCount;
            _nodeMask = nodeMask;
        }

        internal DescriptorLease Allocate()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                foreach (DescriptorPage page in _pages)
                {
                    if (page.TryAllocate(out uint slot))
                    {
                        try
                        {
                            return new DescriptorLease(this, page, slot);
                        }
                        catch
                        {
                            page.Return(slot);
                            throw;
                        }
                    }
                }

                if (_pages.Count >= _maximumHeapCount)
                {
                    throw new GraphicsException(
                        GraphicsError.OutOfMemory,
                        $"The D3D12 {_type} descriptor capacity is exhausted.");
                }

                DescriptorPage newPage = new(
                    _device,
                    _type,
                    _capacity,
                    _shaderVisible,
                    _nodeMask);
                bool added = false;
                try
                {
                    _pages.Add(newPage);
                    added = true;
                    if (!newPage.TryAllocate(out uint newSlot))
                        throw new InvalidOperationException("A new descriptor heap has no free slot.");
                    try
                    {
                        return new DescriptorLease(this, newPage, newSlot);
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

        internal void Return(DescriptorPage page, uint slot)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                page.Return(slot);
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
        private const uint AllocatedSlot = uint.MaxValue;
        private const uint EndOfFreeList = uint.MaxValue - 1;

        private readonly uint[] _free;
        private readonly uint _capacity;
        private readonly uint _increment;
        private uint _freeHead = EndOfFreeList;
        private int _freeCount;
        private uint _cursor;
        private ID3D12DescriptorHeap* _heap;

        internal DescriptorPage(
            D3D12Device device,
            DescriptorHeapType type,
            uint capacity,
            bool shaderVisible,
            uint nodeMask)
        {
            _free = new uint[checked((int)capacity)];
            _capacity = capacity;
            _increment = device.Native->GetDescriptorHandleIncrementSize(type);
            DescriptorHeapDesc description = new(
                type,
                capacity,
                shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
                nodeMask);
            Guid iid = ID3D12DescriptorHeap.Guid;
            ID3D12DescriptorHeap* heap = null;
            ThrowIfFailed(
                device,
                device.Native->CreateDescriptorHeap(
                    &description,
                    &iid,
                    (void**)&heap),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap");
            _heap = heap;
            SetNativeName(
                heap,
                $"{type} Descriptor Heap (capacity={capacity}, nodeMask=0x{nodeMask:X})");
            CpuStart = _heap->GetCPUDescriptorHandleForHeapStart();
            GpuStart = shaderVisible
                ? _heap->GetGPUDescriptorHandleForHeapStart()
                : default;
        }

        internal ID3D12DescriptorHeap* Heap => _heap;
        internal CpuDescriptorHandle CpuStart { get; }
        internal GpuDescriptorHandle GpuStart { get; }

        internal bool TryAllocate(out uint slot)
        {
            if (_freeCount != 0)
            {
                slot = _freeHead;
                _freeHead = _free[(int)slot];
                _free[(int)slot] = AllocatedSlot;
                _freeCount--;
                return true;
            }
            if (_cursor >= _capacity)
            {
                slot = 0;
                return false;
            }

            slot = _cursor++;
            _free[(int)slot] = AllocatedSlot;
            return true;
        }

        internal void Return(uint slot)
        {
            if (slot >= _cursor)
                return;
            int index = (int)slot;
            if (_free[index] != AllocatedSlot || _freeCount >= _free.Length)
                return;
            _free[index] = _freeHead;
            _freeHead = slot;
            _freeCount++;
        }

        internal CpuDescriptorHandle GetCpu(uint slot) =>
            new(CpuStart.Ptr + checked((nuint)(slot * _increment)));

        internal GpuDescriptorHandle GetGpu(uint slot) =>
            new(GpuStart.Ptr + checked((ulong)slot * _increment));

        public void Dispose()
        {
            ID3D12DescriptorHeap* heap = _heap;
            _heap = null;
            if (heap is not null)
                _ = heap->Release();
        }
    }

    private sealed class DescriptorLease
    {
        private DescriptorAllocator? _owner;
        private readonly DescriptorPage _page;
        private readonly uint _slot;
        private int _references = 1;

        internal DescriptorLease(
            DescriptorAllocator owner,
            DescriptorPage page,
            uint slot)
        {
            _owner = owner;
            _page = page;
            _slot = slot;
        }

        internal CpuDescriptorHandle Cpu => _page.GetCpu(_slot);
        internal GpuDescriptorHandle Gpu => _page.GetGpu(_slot);
        internal ID3D12DescriptorHeap* Heap => _page.Heap;
        internal uint Index => _slot;

        internal void Retain()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(
                    ref _references,
                    checked(current + 1),
                    current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }
            throw new ObjectDisposedException(nameof(DescriptorLease));
        }

        internal void Release()
        {
            int remaining = Interlocked.Decrement(ref _references);
            if (remaining != 0)
                return;
            DescriptorAllocator? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Return(_page, _slot);
        }
    }

    private sealed class ViewReferences
    {
        private readonly D3D12Device _device;
        private readonly DescriptorLease _descriptor;
        private NativeLease? _resource;
        private NativeLease? _secondaryResource;

        internal ViewReferences(
            D3D12Device device,
            DescriptorLease descriptor,
            NativeLease? resource = null,
            NativeLease? secondaryResource = null)
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

        internal void Release(GraphicsObject owner)
        {
            _descriptor.Release();
            Interlocked.Exchange(ref _secondaryResource, null)?.Release();
            Interlocked.Exchange(ref _resource, null)?.Release();
            _device.UnregisterChild(owner);
        }
    }
}
