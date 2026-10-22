using SomeEngine.Terrains;
using SomeEngine.Render.Terrain;

namespace SomeEngine.Runtime;

/// <summary>Installs terrain game systems, request extraction and GPU field work.</summary>
public sealed class TerrainGeometryFeature(TerrainScene scene) : IApplicationGeometryFeature
{
    public IDisposable Install(ApplicationGeometryContext context)
    {
        var data = new RenderTerrainData(context.Backend, context.Device, context.Graph);
        context.AddGameSystem(new TerrainCommitSystem(scene));
        context.AddGameSystem(new TerrainLodSystem(scene));
        context.AddGameSystem(new TerrainMeshSystem(scene));
        context.AddExtractor(new TerrainRenderExtractor());
        context.AddPrepareSystem(data);
        context.AddFrameSystem(data);
        context.AddCompletionObserver(data);
        return data;
    }
}
