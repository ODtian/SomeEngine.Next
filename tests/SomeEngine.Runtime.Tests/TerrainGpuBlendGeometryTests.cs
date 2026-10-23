using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuBlendGeometryTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuNestedSurfaceMatchesCpuAfterHeightAndVoxelEdits()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuBlendTerrainExample gpu = await GpuBlendTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        var cpu = new BlendTerrainExample(scheduler);
        var bounds = new TerrainBounds(new(0, -4, 0), new(8, 4, 8));
        var center = new Vector3(4, 0, 4);
        HeightField gpuSecondHeight = gpu.Heights.Terrain.CreateHeightField("Upper source", new HeightGrid(5, 5, 4, 2f), .5f);
        gpu.Heights.Terrain.Surface = new BlendSurface(gpu.Heights.Terrain.Surface!, new HeightSurface(gpuSecondHeight), .25f);
        HeightField cpuSecondHeight = cpu.Heights.Terrain.CreateHeightField("Upper source", new HeightGrid(5, 5, 4, 2f), .5f);
        cpu.Heights.Terrain.Surface = new BlendSurface(cpu.Heights.Terrain.Surface!, new HeightSurface(cpuSecondHeight), .25f);
        SceneImage? before = null, heightPicture = null, voxelPicture = null;
        SceneImage? expectedBefore = null, expectedHeight = null, expectedVoxel = null;
        Task<TerrainSurfaceRead>? heightSurface = null, voxelSurface = null;
        TerrainEdit heightEdit = default, voxelEdit = default;
        TerrainGpuBuild heightBuild = default, voxelBuild = default;
        TerrainChunk[] chunks = [];
        int placement = 0;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "14"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    placement = gpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 5 or 10) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 3) heightEdit = gpu.AddHeight(1f);
                if (frame.Index == 4) heightEdit.Completion.Complete();
                if (frame.Index == 7)
                {
                    heightBuild = gpu.Scene.GetGpuBuild(gpu.Heights.Terrain);
                    heightSurface = gpu.Heights.Terrain.Surface!.ReadCpuAsync(bounds).AsTask();
                    frame.Scene.CaptureImage(image => heightPicture = image);
                }
                if (frame.Index == 8) voxelEdit = gpu.AddSphereDelta(center, 3f, -1f);
                if (frame.Index == 9) voxelEdit.Completion.Complete();
                if (frame.Index == 12)
                {
                    voxelBuild = gpu.Scene.GetGpuBuild(gpu.Heights.Terrain);
                    chunks = gpu.Scene.GetChunks(placement);
                    voxelSurface = gpu.Heights.Terrain.Surface!.ReadCpuAsync(bounds).AsTask();
                    frame.Scene.CaptureImage(image => voxelPicture = image);
                }
            },
        };
        gpu.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.True(result == 0, completed?.Error?.ToString());
        TerrainSurfaceRead heightValues = await heightSurface!;
        TerrainSurfaceRead voxelValues = await voxelSurface!;
        Assert.Equal(heightEdit.Revision, heightValues.Revision);
        Assert.Equal(voxelEdit.Revision, voxelValues.Revision);
        Assert.Equal(heightEdit.Revision, heightBuild.Revision);
        Assert.Equal(voxelEdit.Revision, voxelBuild.Revision);
        Assert.True(heightBuild.Completion.IsCompleted);
        Assert.True(heightBuild.Visibility.IsCompleted);
        Assert.True(voxelBuild.Completion.IsCompleted);
        Assert.True(voxelBuild.Visibility.IsCompleted);
        Assert.All(chunks, chunk => Assert.Equal(voxelEdit.Revision, chunk.Revision));
        Assert.Equal(-.125f, heightValues.Sample(center));
        Assert.Equal(-.875f, heightValues.Sample(new(0, 0, 4)));
        Assert.Equal(-.875f, voxelValues.Sample(center));

        TerrainSurfaceRead? expectedHeightValues = null, expectedVoxelValues = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "14"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 4 or 9) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 3) cpu.Heights.EditCpu(new HeightDeltaBrush(cpu.Heights.Grid.Range, 1f));
                if (frame.Index == 7)
                {
                    expectedHeightValues = cpu.Heights.Terrain.Surface!.ReadCpuAsync(bounds).GetAwaiter().GetResult();
                    frame.Scene.CaptureImage(image => expectedHeight = image);
                }
                if (frame.Index == 8) cpu.Voxels.EditCpu(new VoxelSphereDeltaBrush(cpu.Voxels.Grid, center, 3f, -1f));
                if (frame.Index == 12)
                {
                    expectedVoxelValues = cpu.Heights.Terrain.Surface!.ReadCpuAsync(bounds).GetAwaiter().GetResult();
                    frame.Scene.CaptureImage(image => expectedVoxel = image);
                }
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));
        foreach (Vector3 position in new Vector3[] { center, new(0, 0, 4), new(1, 0, 4), new(4.5f, 1, 4) })
        {
            Assert.Equal(expectedHeightValues!.Sample(position), heightValues.Sample(position));
            Assert.Equal(expectedVoxelValues!.Sample(position), voxelValues.Sample(position));
        }
        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedHeight!, heightPicture!);
        SceneImages.AssertEqual(expectedVoxel!, voxelPicture!);
        SceneImages.AssertChanged(before!, heightPicture!);
        SceneImages.AssertChanged(heightPicture!, voxelPicture!);
    }
}
