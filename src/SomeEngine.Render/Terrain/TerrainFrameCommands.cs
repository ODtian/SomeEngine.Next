using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Terrain;

internal ref struct TerrainFrameCommands(RenderGraphFrame frame, GraphRegionId region)
{
    private RenderGraphFrame _frame = frame;
    public GraphPassId AddComputePass<T>(string label, in PassQueueSelection queue, in T state,
        in PassOptions options, PassDeclaration<T> declaration, ComputeFrameCallback<T> callback)
        => _frame.AddComputePass(label, queue, state, options, declaration, callback, region);
    public GraphPassId AddCopyPass<T>(string label, in PassQueueSelection queue, in T state,
        in PassOptions options, PassDeclaration<T> declaration, CopyFrameCallback<T> callback)
        => _frame.AddCopyPass(label, queue, state, options, declaration, callback, region);
    public void OrderAfter(GraphPassId pass, GraphPassId predecessor) => _frame.OrderAfter(pass, predecessor);
}
