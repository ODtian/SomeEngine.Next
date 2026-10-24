using SomeEngine.Terrains;
using SomeEngine.Render.Terrain;

namespace SomeEngine.Runtime;

/// <summary>Installs terrain game systems, request extraction and GPU field work.</summary>
public sealed class TerrainGeometryFeature(TerrainScene scene) : IApplicationGeometryFeature
{
    public IDisposable? Install(ApplicationGeometryContext context)
    {
        RenderTerrainData data = context.GetTerrainData();
        context.AddGameSystem(new TerrainCommitSystem(scene));
        context.AddGameSystem(new TerrainLodSystem(scene, () => context.CameraPosition));
        context.AddGameSystem(new TerrainMeshSystem(scene));
        context.AddGameShutdown(scene.FinishBuilds);
        if (scene.GpuAdapter is not null)
        {
            var geometry = new RenderTerrainGeometry(context.Backend, context.Device, context.Cluster, context.Graph, data, scene);
            context.AddExtractor(new TerrainGeometryExtractor(scene.RenderKey));
            context.AddPrepareSystem(geometry);
            context.AddFrameSystem(geometry);
            context.AddCompletionObserver(geometry);
            return geometry;
        }
        return null;
    }
}
