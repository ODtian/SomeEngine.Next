using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// Device-owned storage for persistent CPU descriptors. Pages stay alive for the device lifetime,
/// while individual slots return only after the owning view or sampler reaches its RHI retirement
/// point. This avoids creating one native descriptor heap per logical descriptor.
/// </summary>
internal sealed class CpuDescriptorPool : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly object _gate = new();
    private readonly Dictionary<DescriptorHeapType, Bucket> _buckets = [];
    private bool _disposed;
    private int _heapCount;
    private int _outstanding;

    public CpuDescriptorPool(ID3D12Device device) =>
        _device = device ?? throw new ArgumentNullException(nameof(device));

    public int HeapCount
    {
        get { lock (_gate) return _heapCount; }
    }

    public int OutstandingDescriptorCount
    {
        get { lock (_gate) return _outstanding; }
    }

    public NativeCpuDescriptor Allocate(DescriptorHeapType type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_buckets.TryGetValue(type, out Bucket? bucket))
            {
                bucket = new Bucket(DescriptorCapacity(type));
                _buckets.Add(type, bucket);
            }

            for (int pageIndex = 0; pageIndex < bucket.Pages.Count; pageIndex++)
            {
                if (bucket.Pages[pageIndex].TryAllocate(out int slot, out CpuDescriptorHandle handle))
                {
                    _outstanding = checked(_outstanding + 1);
                    return new NativeCpuDescriptor(this, type, pageIndex, slot, handle);
                }
            }

            Page page = new(_device, type, bucket.Capacity);
            bucket.Pages.Add(page);
            _heapCount = checked(_heapCount + 1);
            if (!page.TryAllocate(out int newSlot, out CpuDescriptorHandle newHandle))
                throw new InvalidOperationException("A newly created CPU descriptor page could not allocate its first slot.");
            _outstanding = checked(_outstanding + 1);
            return new NativeCpuDescriptor(this, type, bucket.Pages.Count - 1, newSlot, newHandle);
        }
    }

    internal void Release(DescriptorHeapType type, int pageIndex, int slot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_buckets.TryGetValue(type, out Bucket? bucket) || (uint)pageIndex >= (uint)bucket.Pages.Count)
                throw new InvalidOperationException("A CPU descriptor references an unknown pool page.");
            bucket.Pages[pageIndex].Release(slot);
            _outstanding--;
            if (_outstanding < 0) throw new InvalidOperationException("CPU descriptor pool count underflow.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_outstanding != 0)
                throw new InvalidOperationException("CPU descriptor storage cannot be disposed while descriptors are still owned.");
            foreach (Bucket bucket in _buckets.Values)
            foreach (Page page in bucket.Pages)
                page.Dispose();
            _buckets.Clear();
            _heapCount = 0;
            _disposed = true;
        }
    }

    private static int DescriptorCapacity(DescriptorHeapType type) => type switch
    {
        DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView => 1024,
        DescriptorHeapType.Sampler => 256,
        DescriptorHeapType.RenderTargetView => 256,
        DescriptorHeapType.DepthStencilView => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private sealed class Bucket
    {
        public Bucket(int capacity) => Capacity = capacity;

        public int Capacity { get; }
        public List<Page> Pages { get; } = [];
    }

    private sealed class Page : IDisposable
    {
        private readonly int _capacity;
        private readonly int _increment;
        private readonly Stack<int> _free = [];
        private int _cursor;

        public Page(ID3D12Device device, DescriptorHeapType type, int capacity)
        {
            _capacity = capacity;
            Heap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
                type,
                checked((uint)capacity),
                DescriptorHeapFlags.None,
                0));
            _increment = checked((int)device.GetDescriptorHandleIncrementSize(type));
        }

        public ID3D12DescriptorHeap Heap { get; }

        public bool TryAllocate(out int slot, out CpuDescriptorHandle handle)
        {
            if (_free.TryPop(out slot))
            {
                handle = Heap.GetCPUDescriptorHandleForHeapStart() + checked(slot * _increment);
                return true;
            }
            if (_cursor >= _capacity)
            {
                slot = -1;
                handle = default;
                return false;
            }

            slot = _cursor++;
            handle = Heap.GetCPUDescriptorHandleForHeapStart() + checked(slot * _increment);
            return true;
        }

        public void Release(int slot)
        {
            if ((uint)slot >= (uint)_cursor)
                throw new InvalidOperationException("A CPU descriptor slot is outside its allocated page range.");
            _free.Push(slot);
        }

        public void Dispose() => Heap.Dispose();
    }
}

internal sealed class NativeCpuDescriptor : IDisposable
{
    private CpuDescriptorPool? _owner;
    private readonly int _pageIndex;
    private readonly int _slot;

    internal NativeCpuDescriptor(
        CpuDescriptorPool owner,
        DescriptorHeapType type,
        int pageIndex,
        int slot,
        CpuDescriptorHandle handle)
    {
        _owner = owner;
        Type = type;
        _pageIndex = pageIndex;
        _slot = slot;
        Handle = handle;
    }

    public CpuDescriptorHandle Handle { get; }
    public DescriptorHeapType Type { get; }

    public void Dispose()
    {
        CpuDescriptorPool? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(Type, _pageIndex, _slot);
    }
}
