using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>An example of two placements sharing one editable height field.</summary>
public sealed class HeightTerrainExample
{
    public HeightTerrainExample(Scheduler scheduler)
    {
        // A four metre square, one metre sampling and two vertical chunks are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Shared ground", new HeightGrid(5, 5, 4));
        terrain.Surface = new HeightSurface(Heights);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
    }

    public HeightField Heights { get; }
    public TerrainScene Scene { get; }

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    public (int First, int Second) AddPlacements(Material firstMaterial, Material secondMaterial)
    {
        var chunks = new TerrainChunkRange(0, -1, 0, 1, 2, 1);
        int first = Scene.Add(new TerrainPlacement(Heights.Terrain, chunks, [firstMaterial])
        {
            Transform = new TransformQvvs(new Vector3(-5, -2, 6), Quaternion.Identity),
        });
        int second = Scene.Add(new TerrainPlacement(Heights.Terrain, chunks, [secondMaterial])
        {
            Transform = new TransformQvvs(new Vector3(1, -2, 6), Quaternion.Identity),
        });
        return (first, second);
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
