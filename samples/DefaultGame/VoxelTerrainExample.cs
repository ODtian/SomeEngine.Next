using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>An example of a three-dimensional field edited into a movable sampled sphere.</summary>
public sealed class VoxelTerrainExample
{
    public VoxelTerrainExample(Scheduler scheduler)
    {
        // One metre sampling, a unit radius and this finite editing region are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Field = terrain.CreateVoxelField("Signed sphere", new VoxelGrid(4));
        terrain.Surface = new VoxelSurface(Field);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new SphereAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
        WriteSphere(new Vector3(-1, 0, 0));
    }

    public VoxelField Field { get; }
    public TerrainScene Scene { get; }
    public VoxelRegion SampleRegion { get; } = new(-3, -3, -3, 7, 7, 7);

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    public int AddPlacement(Material material) =>
        Scene.Add(new TerrainPlacement(Field.Terrain, new(-1, -1, -1, 2, 2, 2), [material])
        {
            Transform = new TransformQvvs(new Vector3(0, 0, 6), Quaternion.Identity),
        });

    /// <summary>Replaces the example's editing region with samples of a unit sphere at this centre.</summary>
    public TerrainEdit WriteSphere(Vector3 center) => Field.EditCpu(new SphereSamples(SampleRegion, center));

    /// <summary>Adds a GPU sphere delta with linear falloff to this example's sampled surface.</summary>
    public TerrainEdit AddSphereDeltaGpu(Shader shader, Vector3 center, float radius, float delta) =>
        Field.EditGpu(new VoxelSphereDeltaBrushGpu(shader, Field.Grid, center, radius, delta));

    private readonly record struct SphereSamples(VoxelRegion Range, Vector3 Center) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) =>
            Vector3.Distance(source.Grid.SamplePosition(x, y, z), Center) - 1f;
    }

    private sealed class SphereAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point)
        {
            Vector3 normal = point.Normal;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(
                MathF.Abs(normal.Y) < .9f ? Vector3.UnitY : Vector3.UnitZ, normal));
            return new(normal, new(tangent, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        }
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
