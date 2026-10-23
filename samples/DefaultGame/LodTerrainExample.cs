using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Terrains;

namespace DefaultGame;

/// <summary>An example of camera LOD and editable detail crossing a coarse/fine boundary.</summary>
public sealed class LodTerrainExample
{
    public LodTerrainExample(Scheduler scheduler)
    {
        // The 32-by-16 metre area, one metre lattice and refinement distances are example values.
        var terrain = new Terrain(new TerrainEditing(scheduler));
        Heights = terrain.CreateHeightField("Rolling ground", new HeightGrid(33, 17, 8));
        Heights.EditCpu(new GroundHeights(Heights.Grid.Range));
        Voxels = terrain.CreateVoxelField("Editable detail", new VoxelGrid(8), 16f);
        var height = new HeightSurface(Heights);
        height.WriteVoxelsCpu(Voxels, new(0, -2, 0, 33, 13, 17));
        terrain.Surface = new BlendSurface(height, new VoxelSurface(Voxels), .5f,
            new TerrainBounds(new(0, -2, 0), new(32, 10, 16)), 2f);
        Scene = new TerrainScene(new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime), new(4, 1f));
    }

    public HeightField Heights { get; }
    public VoxelField Voxels { get; }
    public TerrainScene Scene { get; }

    public void Install(ApplicationScene application) => application.InstallGeometry(new TerrainGeometryFeature(Scene));

    public TerrainPlacement CreatePlacement(Material material) => new(Heights.Terrain, new(0, 0, 0, 2, 1, 1),
        [material], new TerrainLodDistances(5, 9))
    {
        Transform = new TransformQvvs(new(-16, -4, 10), Quaternion.Identity),
    };

    /// <summary>The sphere position, radius and scalar delta are example brush values.</summary>
    public TerrainEdit RaiseDetail() =>
        Voxels.EditCpu(new VoxelSphereDeltaBrush(Voxels.Grid, new(16, 3, 8), 4f, -2f));

    private readonly record struct GroundHeights(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z) => 3f + .6f * MathF.Sin(x * .35f) * MathF.Cos(z * .3f);
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
