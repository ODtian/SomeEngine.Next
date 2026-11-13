namespace SomeEngine.Render.Instances;

/// <summary>Completes the property writes for one batch after all producer jobs have finished.</summary>
public sealed class RenderInstanceWriteHandle : IDisposable
{
    private RenderInstanceBatchComposition? _composition;
    internal RenderInstanceWriteHandle(RenderInstanceBatchComposition composition) => _composition = composition;

    public bool IsActive => _composition is not null;
    public int Count => RequireComposition().InstanceCount;
    public RenderInstancePropertyLayout Properties => RequireComposition().Authorization;
    public RenderInstanceWriteSlice OpenWrite(RenderInstancePropertyLayout properties) =>
        RequireComposition().OpenWrite(properties);
    public RenderInstanceWriteSlice OpenWrite(RenderInstancePropertyLayout properties, int destinationStart, int count) =>
        RequireComposition().OpenWrite(properties, destinationStart, count);

    public RenderInstanceBatch Publish()
    {
        RenderInstanceBatch batch = RequireComposition().Publish();
        _composition = null;
        return batch;
    }

    public void Dispose() => Interlocked.Exchange(ref _composition, null)?.Dispose();

    private RenderInstanceBatchComposition RequireComposition() => _composition
        ?? throw new ObjectDisposedException(nameof(RenderInstanceWriteHandle));
}
