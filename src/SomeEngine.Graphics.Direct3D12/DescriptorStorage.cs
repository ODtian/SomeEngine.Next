using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
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
        private readonly object _gate = new();
        private readonly List<DescriptorPage> _pages = [];
        private bool _disposed;

        internal DescriptorAllocator(
            D3D12Device device,
            DescriptorHeapType type,
            uint capacity,
            bool shaderVisible,
            int maximumHeapCount)
        {
            _device = device;
            _type = type;
            _capacity = capacity;
            _shaderVisible = shaderVisible;
            _maximumHeapCount = maximumHeapCount;
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
                    _shaderVisible);
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
        private readonly Stack<uint> _free = [];
        private readonly uint _capacity;
        private readonly uint _increment;
        private uint _cursor;
        private ID3D12DescriptorHeap* _heap;

        internal DescriptorPage(
            D3D12Device device,
            DescriptorHeapType type,
            uint capacity,
            bool shaderVisible)
        {
            _capacity = capacity;
            _increment = device.Native->GetDescriptorHandleIncrementSize(type);
            DescriptorHeapDesc description = new(
                type,
                capacity,
                shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
                device.EnabledNodeMask);
            Guid iid = ID3D12DescriptorHeap.Guid;
            ID3D12DescriptorHeap* heap = null;
            NativeCall.ThrowIfFailed(
                device.Native->CreateDescriptorHeap(
                    &description,
                    &iid,
                    (void**)&heap),
                "ID3D12Device::CreateDescriptorHeap");
            _heap = heap;
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
            if (_free.TryPop(out slot))
                return true;
            if (_cursor >= _capacity)
            {
                slot = 0;
                return false;
            }

            slot = _cursor++;
            return true;
        }

        internal void Return(uint slot)
        {
            if (slot >= _cursor)
                return;
            _free.Push(slot);
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

    private sealed class ViewLifetime
    {
        private readonly D3D12Device _device;
        private readonly D3D12Buffer? _buffer;
        private readonly D3D12TextureResource? _texture;
        private readonly D3D12TextureResource? _pairedTexture;
        private readonly DescriptorLease _descriptor;
        private int _released;

        internal ViewLifetime(
            D3D12Device device,
            DescriptorLease descriptor,
            D3D12Buffer? buffer = null,
            D3D12TextureResource? texture = null,
            D3D12TextureResource? pairedTexture = null)
        {
            _device = device;
            _buffer = buffer;
            _texture = texture;
            _pairedTexture = pairedTexture;
            _descriptor = descriptor;
        }

        internal void Release(GraphicsObject owner)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.Descriptors.NotifyDisposed(owner);
            _descriptor.Release();
            _buffer?.UnregisterView(owner);
            _texture?.UnregisterView(owner);
            _pairedTexture?.UnregisterView(owner);
            _device.UnregisterChild(owner);
        }
    }
}
