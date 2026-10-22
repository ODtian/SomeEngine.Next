using System.Numerics;
using DefaultGame;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class BlendTerrainRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task EditingHeightInitializedVoxelsRaisesAContinuousLocalSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new BlendTerrainExample(scheduler);
        VoxelFieldRead initial = await example.Voxels.ReadCpuAsync(example.SampleRegion);
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
        TerrainEdit edit = default;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    example.AddPlacement(material);
                }
                if (frame.Index is 1 or 10) example.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) edit = example.RaiseDetail(2f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);
        Assert.Equal(0, await Application.RunAsync([], application: application));
        VoxelFieldRead current = await example.Voxels.ReadCpuAsync(example.SampleRegion);
        TerrainSurfaceRead surface = await example.Voxels.Terrain.Surface!.ReadCpuAsync(new(new(0, -4, 0), new(8, 4, 8)));
        Assert.Equal(0f, initial[4, 0, 4]);
        Assert.Equal(-2f, current[4, 0, 4]);
        Assert.Equal(edit.Revision, current.Revision);
        Assert.Equal(0f, surface.Sample(new(4, 2, 4)));
        Assert.Equal(0f, surface.Sample(new(1, 1, 4)));
        Assert.Equal(0f, surface.Sample(new(0, 0, 4)));

        var terrain = new Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("Plateau reference", new HeightGrid(9, 9, 4));
        terrain.Surface = new HeightSurface(heights);
        var adapter = new TerrainClusterAdapter<StandardClusterAttributes>(new GroundAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime);
        var scene = new TerrainScene(adapter, new TerrainChunkGrid(4, 1));
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    scene.Add(new TerrainPlacement(terrain, new(0, -1, 0, 2, 2, 2), [material])
                    {
                        Transform = new TransformQvvs(new Vector3(-4, -2, 6), Quaternion.Identity),
                    });
                }
                if (frame.Index is 1 or 10) scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 9) heights.EditCpu(new PlateauHeights(heights.Grid.Range));
                if (frame.Index == 17) frame.Scene.CaptureImage(image => expectedAfter = image);
            },
        };
        reference.InstallGeometry(new TerrainGeometryFeature(scene));
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);
        SceneImages.AssertChanged(before!, after!);
    }

    private readonly record struct PlateauHeights(HeightRegion Range) : IHeightBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(HeightFieldRead source, int x, int z)
        {
            if (x is 0 or 8 || z is 0 or 8) return 0f;
            if (x is 1 or 7 || z is 1 or 7) return 1f;
            return 2f;
        }
    }

    private sealed class GroundAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
