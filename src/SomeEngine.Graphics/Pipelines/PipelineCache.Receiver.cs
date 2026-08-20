namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    PipelineCache CreatePipelineCache(
        Device device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken = default);

    bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken = default);

    void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources,
        CancellationToken cancellationToken = default);
}
