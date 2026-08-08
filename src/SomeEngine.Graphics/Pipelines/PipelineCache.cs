namespace SomeEngine.Graphics;

public readonly ref struct PipelineCacheDesc
{
    public PipelineCacheDesc(ReadOnlySpan<byte> data = default, string? label = null)
    {
        Data = data;
        Label = label;
    }

    public ReadOnlySpan<byte> Data { get; }
    public string? Label { get; }
}

public abstract class PipelineCache : DeviceResource
{
    internal PipelineCache(Device device, string? label)
        : base(device, label)
    {
    }
}
