using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc);

    bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount);

    void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc) =>
        Receiver.CreatePipelineCache(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount) =>
        Receiver.TryGetPipelineCacheData(cache, destination, out requiredByteCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources) =>
        Receiver.MergePipelineCaches(destination, sources);
}
