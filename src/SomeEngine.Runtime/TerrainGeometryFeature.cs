using SomeEngine.Terrains;

namespace SomeEngine.Runtime;

/// <summary>Installs terrain commits, chunk selection and CPU meshing before game-world extraction.</summary>
public sealed class TerrainGeometryFeature(TerrainScene scene) : IApplicationGeometryFeature
{
    public IDisposable? Install(ApplicationGeometryContext context)
    {
        context.AddGameSystem(new TerrainCommitSystem(scene));
        context.AddGameSystem(new TerrainLodSystem(scene));
        context.AddGameSystem(new TerrainMeshSystem(scene));
        return null;
    }
}
