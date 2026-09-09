namespace SomeEngine.RenderGraph;

internal interface IRenderGraphFrameExecutionObserver
{
    void OnRenderGraphFrameFinished(bool executed);
}
