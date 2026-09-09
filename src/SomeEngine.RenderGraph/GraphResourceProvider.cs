using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph;

/// <summary>
/// Declares the resource consumed by a shader parameter at the consumer's access stages.
/// Called when connecting a pass; frame updates supply values through the declared graph inputs.
/// </summary>
public delegate void GraphResourceBinder(
    ref RenderGraphEdit graph,
    ref PassDefinition pass,
    PipelineSync sync);

/// <summary>
/// Resolves named shader resources during pass construction. The provider retains its resources;
/// the graph retains the access declarations created by the returned binder.
/// </summary>
public interface IGraphResourceProvider
{
    bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder);
}
