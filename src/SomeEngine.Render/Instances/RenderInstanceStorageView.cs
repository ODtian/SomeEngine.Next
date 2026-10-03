using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Instances;

/// <summary>Read access to batch graph buffers during a render read scope.</summary>
public ref struct RenderInstanceStorageView
{
    private RenderInstanceResources? _owner;
    private RenderReadScope? _readScope;

    internal RenderInstanceStorageView(
        RenderInstanceResources owner,
        RenderReadScope readScope)
    {
        _owner = owner;
        _readScope = readScope;
    }

    public RenderInstanceBatchView Bind(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout exactShaderLayout)
    {
        RenderInstanceResources owner = RequireOwner();
        return owner.GetBatchView(batch, exactShaderLayout);
    }

    public void Dispose()
    {
        _owner = null;
        _readScope = null;
    }

    private RenderInstanceResources RequireOwner()
    {
        if (_owner is null || _readScope is null || _readScope.IsClosed)
            throw new ObjectDisposedException(nameof(RenderInstanceStorageView));
        return _owner;
    }
}
