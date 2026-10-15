using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

public readonly record struct ApplicationRendering(IGraphicsBackend Backend, Device Device,
    global::SomeEngine.RenderGraph.RenderGraph Graph, int Width, int Height, Matrix4x4 Projection = default);

/// <summary>A persistent color feature consuming the renderer's color and depth.</summary>
public interface IApplicationColorFeature : IDisposable
{
    GraphPassId CompletePass { get; }
    void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady);
    GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth);
}
