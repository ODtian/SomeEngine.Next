namespace SomeEngine.Render.Instances;

/// <summary>
/// Prepare-scoped write capability for one already allocated logical batch. It can expose only
/// declared property slices, and can publish exactly once; it cannot retire other batches or
/// access physical row addresses. Pipeline systems may create disjoint slices, give those values
/// to synchronous jobs, complete the jobs, and then publish this handle. The enclosing prepare
/// update owns the shared storage write scope, so several batch handles may be filled in parallel.
/// </summary>
public sealed class RenderInstanceWriteHandle : IDisposable
{
    private RenderInstanceWriteScope? _scope;
    private RenderInstanceBatchComposition? _composition;

    internal RenderInstanceWriteHandle(
        RenderInstanceWriteScope scope,
        RenderInstanceBatchComposition composition)
    {
        _scope = scope;
        _composition = composition;
    }

    public bool IsActive => Volatile.Read(ref _composition) is not null;

    public int Count => RequireComposition().InstanceCount;

    public RenderInstancePropertyLayout Properties => RequireComposition().Authorization;

    /// <summary>Opens this producer's whole logical batch range.</summary>
    public RenderInstanceWriteSlice OpenWrite(RenderInstancePropertyLayout properties) =>
        RequireComposition().OpenWrite(properties);

    /// <summary>
    /// Opens a logical subrange. Concurrent jobs must receive non-overlapping ranges for every
    /// property they write; the handle deliberately exposes no physical row or buffer offset.
    /// </summary>
    public RenderInstanceWriteSlice OpenWrite(
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count) =>
        RequireComposition().OpenWrite(properties, destinationStart, count);

    /// <summary>
    /// Atomically makes the completed batch available to frame readers. All jobs using slices
    /// from this handle must be complete before publication.
    /// </summary>
    public RenderInstanceBatch Publish()
    {
        RenderInstanceBatchComposition composition = RequireComposition();
        RenderInstanceWriteScope scope = _scope
            ?? throw new ObjectDisposedException(nameof(RenderInstanceWriteHandle));
        RenderInstanceBatch batch = composition.Publish();

        Close(scope);
        return batch;
    }

    public void Dispose()
    {
        RenderInstanceBatchComposition? composition =
            Interlocked.Exchange(ref _composition, null);
        _scope = null;
        composition?.Dispose();
    }

    private RenderInstanceBatchComposition RequireComposition() =>
        Volatile.Read(ref _composition)
        ?? throw new ObjectDisposedException(nameof(RenderInstanceWriteHandle));

    private void Close(RenderInstanceWriteScope scope)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _scope, null), scope))
            throw new InvalidOperationException("Render-instance write-scope ownership was lost.");
        _composition = null;
    }
}
