namespace SomeEngine.RenderGraph;

public sealed class RenderGraphLostException : InvalidOperationException
{
    public RenderGraphLostException() : base("The render graph is lost after an execution failure.") { }
}
