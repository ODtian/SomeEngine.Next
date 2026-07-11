using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// Owns the one shader-visible resource heap and one shader-visible sampler heap used by a
/// command allocation. Descriptor blocks are append-only for an entire recording and are reset
/// only after the allocation's submission fence completes.
/// </summary>
internal sealed class CommandDescriptorArena : IDisposable
{
    private readonly int _resourceCapacity;
    private readonly int _samplerCapacity;
    private readonly int _resourceIncrement;
    private readonly int _samplerIncrement;
    private int _resourceCursor;
    private int _samplerCursor;

    public CommandDescriptorArena(ID3D12Device device, int resourceCapacity, int samplerCapacity)
    {
        ArgumentNullException.ThrowIfNull(device);
        _resourceCapacity = resourceCapacity;
        _samplerCapacity = samplerCapacity;
        ResourceHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            checked((uint)resourceCapacity),
            DescriptorHeapFlags.ShaderVisible,
            0));
        try
        {
            SamplerHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
                DescriptorHeapType.Sampler,
                checked((uint)samplerCapacity),
                DescriptorHeapFlags.ShaderVisible,
                0));
        }
        catch
        {
            ResourceHeap.Dispose();
            throw;
        }
        _resourceIncrement = checked((int)device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
        _samplerIncrement = checked((int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler));
        Heaps = [ResourceHeap, SamplerHeap];
    }

    public ID3D12DescriptorHeap ResourceHeap { get; }
    public ID3D12DescriptorHeap SamplerHeap { get; }
    public ID3D12DescriptorHeap[] Heaps { get; }

    public DescriptorBlock AllocateResources(int count) => Allocate(
        count,
        ref _resourceCursor,
        _resourceCapacity,
        _resourceIncrement,
        ResourceHeap,
        "CBV/SRV/UAV");

    public DescriptorBlock AllocateSamplers(int count) => Allocate(
        count,
        ref _samplerCursor,
        _samplerCapacity,
        _samplerIncrement,
        SamplerHeap,
        "sampler");

    public void Reset()
    {
        _resourceCursor = 0;
        _samplerCursor = 0;
    }

    public void Dispose()
    {
        SamplerHeap.Dispose();
        ResourceHeap.Dispose();
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
                $"The command-list {kind} descriptor heap is exhausted: requested {count}, " +
                $"remaining {capacity - cursor}, capacity {capacity}. Increase the corresponding D3D12 Options capacity.");
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
