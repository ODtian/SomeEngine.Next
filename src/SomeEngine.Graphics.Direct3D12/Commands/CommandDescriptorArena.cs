using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// Owns the shader-visible descriptor-heap pages referenced by one command allocation.
/// Pages are append-only while the allocation is in flight. A rollover switches to a fresh
/// resource/sampler heap pair; the command context then replays every active root table from
/// its CPU descriptor sources. All pages stay alive until the allocation's submission fence
/// completes and <see cref="Reset"/> is called.
/// </summary>
internal sealed class CommandDescriptorArena : IDisposable
{
    private readonly ID3D12Device _device;
    private readonly int _defaultResourceCapacity;
    private readonly int _defaultSamplerCapacity;
    private readonly int _resourceIncrement;
    private readonly int _samplerIncrement;
    private readonly List<HeapPair> _pages = [];
    private HeapPair _current;
    private int _resourceCursor;
    private int _samplerCursor;

    public CommandDescriptorArena(ID3D12Device device, int resourceCapacity, int samplerCapacity)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (resourceCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        if (samplerCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(samplerCapacity));

        _device = device;
        _defaultResourceCapacity = resourceCapacity;
        _defaultSamplerCapacity = samplerCapacity;
        _resourceIncrement = checked((int)device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
        _samplerIncrement = checked((int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler));
        _current = CreatePair(resourceCapacity, samplerCapacity);
        _pages.Add(_current);
    }

    public ID3D12DescriptorHeap[] Heaps => _current.Heaps;
    internal int PageCount => _pages.Count;

    public bool HasCapacity(int resourceCount, int samplerCount)
    {
        if (resourceCount < 0) throw new ArgumentOutOfRangeException(nameof(resourceCount));
        if (samplerCount < 0) throw new ArgumentOutOfRangeException(nameof(samplerCount));
        return _resourceCursor <= _current.ResourceCapacity - resourceCount &&
               _samplerCursor <= _current.SamplerCapacity - samplerCount;
    }

    /// <summary>
    /// Starts a new shader-visible heap page large enough to rematerialize all active tables.
    /// The previous page remains owned by this arena until GPU completion returns the command
    /// allocation to the device.
    /// </summary>
    public void RollOver(int activeResourceCount, int activeSamplerCount)
    {
        if (activeResourceCount < 0) throw new ArgumentOutOfRangeException(nameof(activeResourceCount));
        if (activeSamplerCount < 0) throw new ArgumentOutOfRangeException(nameof(activeSamplerCount));

        int resourceCapacity = Math.Max(_defaultResourceCapacity, Math.Max(1, activeResourceCount));
        int samplerCapacity = Math.Max(_defaultSamplerCapacity, Math.Max(1, activeSamplerCount));
        _current = CreatePair(resourceCapacity, samplerCapacity);
        _pages.Add(_current);
        _resourceCursor = 0;
        _samplerCursor = 0;
    }

    public DescriptorBlock AllocateResources(int count) => Allocate(
        count,
        ref _resourceCursor,
        _current.ResourceCapacity,
        _resourceIncrement,
        _current.ResourceHeap,
        "CBV/SRV/UAV");

    public DescriptorBlock AllocateSamplers(int count) => Allocate(
        count,
        ref _samplerCursor,
        _current.SamplerCapacity,
        _samplerIncrement,
        _current.SamplerHeap,
        "sampler");

    public void Reset()
    {
        // Device.CollectGarbage only makes an allocation available after its submission fence
        // completes, so releasing rollover pages here cannot race the GPU.
        for (int index = _pages.Count - 1; index > 0; index--)
        {
            _pages[index].Dispose();
            _pages.RemoveAt(index);
        }

        _current = _pages[0];
        _resourceCursor = 0;
        _samplerCursor = 0;
    }

    public void Dispose()
    {
        for (int index = _pages.Count - 1; index >= 0; index--) _pages[index].Dispose();
        _pages.Clear();
    }

    private HeapPair CreatePair(int resourceCapacity, int samplerCapacity)
    {
        ID3D12DescriptorHeap resourceHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
            new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                checked((uint)resourceCapacity),
                DescriptorHeapFlags.ShaderVisible,
                0));
        try
        {
            ID3D12DescriptorHeap samplerHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(
                    DescriptorHeapType.Sampler,
                    checked((uint)samplerCapacity),
                    DescriptorHeapFlags.ShaderVisible,
                    0));
            return new HeapPair(resourceHeap, samplerHeap, resourceCapacity, samplerCapacity);
        }
        catch
        {
            resourceHeap.Dispose();
            throw;
        }
    }

    private static DescriptorBlock Allocate(
        int count,
        ref int cursor,
        int capacity,
        int increment,
        ID3D12DescriptorHeap heap,
        string kind)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return default;
        if (cursor > capacity - count)
        {
            throw new InvalidOperationException(
                $"The current command-list {kind} descriptor page cannot hold {count} additional descriptor(s). " +
                "CommandContext must roll over and replay active descriptor tables before allocating.");
        }

        int byteOffset = checked(cursor * increment);
        DescriptorBlock result = new(
            heap.GetCPUDescriptorHandleForHeapStart() + byteOffset,
            heap.GetGPUDescriptorHandleForHeapStart() + byteOffset,
            increment,
            count);
        cursor += count;
        return result;
    }

    private sealed class HeapPair : IDisposable
    {
        public HeapPair(
            ID3D12DescriptorHeap resourceHeap,
            ID3D12DescriptorHeap samplerHeap,
            int resourceCapacity,
            int samplerCapacity)
        {
            ResourceHeap = resourceHeap;
            SamplerHeap = samplerHeap;
            ResourceCapacity = resourceCapacity;
            SamplerCapacity = samplerCapacity;
            Heaps = [resourceHeap, samplerHeap];
        }

        public ID3D12DescriptorHeap ResourceHeap { get; }
        public ID3D12DescriptorHeap SamplerHeap { get; }
        public int ResourceCapacity { get; }
        public int SamplerCapacity { get; }
        public ID3D12DescriptorHeap[] Heaps { get; }

        public void Dispose()
        {
            SamplerHeap.Dispose();
            ResourceHeap.Dispose();
        }
    }
}

internal readonly record struct DescriptorBlock(
    CpuDescriptorHandle Cpu,
    GpuDescriptorHandle Gpu,
    int Increment,
    int Count)
{
    public CpuDescriptorHandle CpuAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return Cpu + checked(index * Increment);
    }

    public GpuDescriptorHandle GpuAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return Gpu + checked(index * Increment);
    }
}
