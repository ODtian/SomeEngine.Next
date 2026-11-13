namespace SomeEngine.Render.Instances;

/// <summary>Read access to a batch's graph ranges and CPU property bytes.</summary>
public ref struct RenderInstanceStorageView
{
    private RenderInstanceData? _data;
    internal RenderInstanceStorageView(RenderInstanceData data) => _data = data;
    public RenderInstanceBatchView Bind(RenderInstanceBatch batch, RenderInstancePropertyLayout shaderLayout) =>
        (_data ?? throw new ObjectDisposedException(nameof(RenderInstanceStorageView))).GetBatchView(batch, shaderLayout);
    public void Dispose() => _data = null;
}
