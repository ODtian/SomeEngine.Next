using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Scoped read-only access to the published instance store. Disposing it ends command-recording
/// use for the storage timeline; it owns neither batches nor GPU resources.
/// </summary>
public ref struct RenderInstanceStorageView
{
    private RenderInstanceResources? _owner;
    private RenderFrameUseLease? _frameUse;

    internal RenderInstanceStorageView(
        RenderInstanceResources owner,
        RenderFrameUseLease frameUse)
    {
        _owner = owner;
        _frameUse = frameUse;
    }

    public RenderInstanceBatchView Bind(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout exactShaderLayout)
    {
        RenderInstanceResources owner = RequireOwner();
        return owner.GetBatchView(
            batch,
            exactShaderLayout,
            _frameUse ?? throw new ObjectDisposedException(nameof(RenderInstanceStorageView)));
    }

    public void Dispose()
    {
        _owner = null;
        RenderFrameUseLease? frameUse = _frameUse;
        _frameUse = null;
        frameUse?.Dispose();
    }

    private RenderInstanceResources RequireOwner()
    {
        if (_owner is null || _frameUse is null || _frameUse.IsClosed)
            throw new ObjectDisposedException(nameof(RenderInstanceStorageView));
        return _owner;
    }
}
