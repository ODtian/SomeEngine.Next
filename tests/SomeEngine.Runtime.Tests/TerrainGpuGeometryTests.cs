using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuGeometryTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuHeightGeometrySwitchesTheCompletedBatchToTheCpuPicture()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuHeightTerrainExample gpu = await GpuHeightTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        var cpu = new HeightTerrainExample(scheduler);
        SceneImage? before = null, building = null, after = null, expectedBefore = null, expectedAfter = null;
        TerrainEdit edit = default;
        TerrainGpuBuild build = default;
        Task<HeightFieldRead>? read = null;
        int placement = 0;
        TerrainChunk[] chunks = [];
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
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    placement = gpu.AddPlacements(red, green).First;
                }
                if (frame.Index is 1 or 5) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 3)
                {
                    edit = gpu.AddHeight(1f);
                    frame.Scene.CaptureImage(image => building = image);
                }
                if (frame.Index == 4) edit.Completion.Complete();
                if (frame.Index == 7)
                {
                    build = gpu.Scene.GetGpuBuild(gpu.Heights.Terrain);
                    chunks = gpu.Scene.GetChunks(placement);
                    read = gpu.Heights.ReadCpuAsync(gpu.Heights.Grid.Range).AsTask();
                    frame.Scene.CaptureImage(image => after = image);
                }
            },
        };
        gpu.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.True(result == 0, completed?.Error?.ToString());
        HeightFieldRead values = await read!;
        Assert.Equal(1f, values[2, 2]);
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
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacements(red, green);
                }
                if (frame.Index is 1 or 4) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 3) cpu.Heights.EditCpu(new HeightDeltaBrush(cpu.Heights.Grid.Range, 1f));
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
