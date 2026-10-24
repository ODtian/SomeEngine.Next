using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuVoxelGeometryTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuVoxelGeometryKeepsTheCurrentBatchUntilTheChangedSurfaceIsReady()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuVoxelTerrainExample gpu = await GpuVoxelTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        var cpu = new VoxelTerrainExample(scheduler);
        await gpu.Field.ReadCpuAsync(gpu.SampleRegion);
        var center = new Vector3(-1, 0, 0);
        SceneImage? before = null, building = null, after = null, expectedBefore = null, expectedAfter = null;
        TerrainEdit edit = default;
        TerrainGpuBuild build = default;
        TerrainChunk[] chunks = [];
        int placement = 0;
        Task<VoxelFieldRead>? read = null;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "9"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    placement = gpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 5) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 3)
                {
                    edit = gpu.AddSphereDelta(center, 2f, -1f);
                    frame.Scene.CaptureImage(image => building = image);
                }
                if (frame.Index == 4) edit.Completion.Complete();
                if (frame.Index == 7)
                {
                    build = gpu.Scene.GetGpuBuild(gpu.Field.Terrain);
                    chunks = gpu.Scene.GetChunks(placement);
                    read = gpu.Field.ReadCpuAsync(gpu.SampleRegion).AsTask();
                    frame.Scene.CaptureImage(image => after = image);
                }
            },
        };
        gpu.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.True(result == 0, completed?.Error?.ToString());
        VoxelFieldRead values = await read!;
        Assert.Equal(-2f, values[-1, 0, 0]);
        Assert.Equal(edit.Revision, values.Revision);
        Assert.Equal(edit.Revision, build.Revision);
        Assert.True(build.Completion.IsCompleted);
        Assert.True(build.Visibility.IsCompleted);
        Assert.All(chunks, chunk => Assert.Equal(edit.Revision, chunk.Revision));

        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "9"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 4) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 3) cpu.Field.EditCpu(new VoxelSphereDeltaBrush(cpu.Field.Grid, center, 2f, -1f));
                if (frame.Index == 7) frame.Scene.CaptureImage(image => expectedAfter = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));
        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(before!, building!);
        SceneImages.AssertEqual(expectedAfter!, after!);
        SceneImages.AssertChanged(before!, after!);
    }
}
