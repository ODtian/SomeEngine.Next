using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Render;
using SomeEngine.Assets;
using SomeEngine.Resources;
using SomeEngine.Render.Features.Decals;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Layers;

namespace SomeEngine.Runtime;

public readonly record struct ApplicationRendering(IGraphicsBackend Backend, Device Device,
    global::SomeEngine.RenderGraph.RenderGraph Graph, int Width, int Height, Matrix4x4 Projection = default,
    GraphRegionId Region = default)
{
    public LayerStencil? Stencil => Layers?.Stencil;
    public uint ViewCount { get; init; } = 1;
    public Func<GraphPassId> ShadowRequestPass { get; init; } = static () => default;
    public Func<IReadOnlyList<GraphPassId>> MaterialPasses { get; init; } = static () => [];
    public ShaderLibrary ShaderLibrary { get; init; } = null!;
    public AssetLoader Files { get; init; } = null!;
    public RenderPipelines Pipelines { get; init; } = null!;
    public RenderGeometryResources? Geometry { get; init; }
    public IRenderMaterialResources? Materials { get; init; }
    public MaterialDecalFeature? Decals { get; init; }
    public VirtualShadowFeature? Shadows { get; init; }
    public LayerComposition? Layers { get; init; }

}

/// <summary>A persistent color feature consuming the renderer's color and depth.</summary>
public interface IApplicationColorFeature : IDisposable
{
    GraphPassId CompletePass { get; }
    void ReplaceShader(Shader previous, Shader next) { }
    void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady);
    GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth);
}

public interface IApplicationFrameFeature : IDisposable
{
    void Reconcile();
    void Record(ref RenderGraphFrame frame, int simulationFrame);
}
