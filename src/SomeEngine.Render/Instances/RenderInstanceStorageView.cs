using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Instances;

/// <summary>Read access to batch graph buffers during a render frame.</summary>
public ref struct RenderInstanceStorageView
{
    private RenderInstanceResources? _owner;
    private RenderFrame? _frame;

    internal RenderInstanceStorageView(
        RenderInstanceResources owner,
        RenderFrame frame)
    {
        _owner = owner;
        _frame = frame;
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
        _frame = null;
    }

    private RenderInstanceResources RequireOwner()
    {
        if (_owner is null || _frame is null || _frame.IsClosed)
            throw new ObjectDisposedException(nameof(RenderInstanceStorageView));
        return _owner;
    }
}
