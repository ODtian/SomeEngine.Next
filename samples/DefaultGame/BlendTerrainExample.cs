using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>An example of editable local voxel detail initialized from a height surface.</summary>
public sealed class BlendTerrainExample
{
    public BlendTerrainExample(Scheduler scheduler)
    {
        // Eight metres, one metre sampling and a two metre blend border are example choices.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Ground", new HeightGrid(9, 9, 4));
        Voxels = terrain.CreateVoxelField("Local detail", new VoxelGrid(4));
        var height = new HeightSurface(Heights);
        height.WriteVoxelsCpu(Voxels, SampleRegion);
        terrain.Surface = new BlendSurface(height, new VoxelSurface(Voxels), 1f,
            new TerrainBounds(new(0, -8, 0), new(8, 8, 8)), 2f);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        Scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
    }

    public HeightField Heights { get; }
    public VoxelField Voxels { get; }
    public VoxelRegion SampleRegion { get; } = new(0, -8, 0, 9, 17, 9);
    public TerrainScene Scene { get; }

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    /// <summary>Subtracts a height from the example's voxel samples, raising the locally blended surface.</summary>
    public TerrainEdit RaiseDetail(float height) => Voxels.EditCpu(new ScalarDelta(SampleRegion, -height));

    public int AddPlacement(Material material) => Scene.Add(new TerrainPlacement(Voxels.Terrain,
        new(0, -1, 0, 2, 2, 2), [material])
    {
        Transform = new TransformQvvs(new Vector3(-4, -2, 6), Quaternion.Identity),
    });

    private readonly record struct ScalarDelta(VoxelRegion Range, float Delta) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) => source[x, y, z] + Delta;
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
