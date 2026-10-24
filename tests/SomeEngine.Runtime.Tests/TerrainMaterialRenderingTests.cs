using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainMaterialRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task CpuAndGpuMaterialFieldsProduceTheSameDisplacedMaterialGroups()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using TerrainMaterialExample gpu = await TerrainMaterialExample.CreateGpuAsync(scheduler, content.DirectoryPath);
        using TerrainMaterialExample cpu = await TerrainMaterialExample.CreateCpuAsync(scheduler, content.DirectoryPath);
        SceneImage? before = null, painted = null, displaced = null;
        SceneImage? expectedBefore = null, expectedPainted = null, expectedDisplaced = null;
        TerrainEdit edit = default;
        TerrainGpuBuild build = default;
        TerrainChunk[] chunks = [];
        int placement = 0;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    placement = gpu.AddPlacement();
                    frame.Scene.SetCameraTransform(new(0, 2, 2), Quaternion.CreateFromYawPitchRoll(0, .4f, 0));
                }
                if (frame.Index is 1 or 5) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 3) edit = gpu.RaiseLiftGpu(.5f);
                if (frame.Index == 4) edit.Completion.Complete();
                if (frame.Index == 7)
                {
                    chunks = gpu.Scene.GetChunks(placement);
                    build = gpu.Scene.GetGpuBuild(gpu.Lift.Terrain);
                    frame.Scene.CaptureImage(image => painted = image);
                }
                if (frame.Index == 8)
                    foreach (TerrainDisplacementMaterial material in gpu.Materials) material.Displacement = 2f;
                if (frame.Index == 10) frame.Scene.CaptureImage(image => displaced = image);
            },
        };
        gpu.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.True(result == 0, completed?.Error?.ToString());
        Assert.Equal(edit.Revision, build.Revision);
        Assert.True(build.Completion.IsCompleted);
        Assert.True(build.Visibility.IsCompleted);
        Assert.All(chunks, chunk => Assert.Equal(edit.Revision, chunk.Revision));
        Assert.All(gpu.Materials, material => Assert.Equal(2f, material.BoundsExpansion));

        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacement();
                    frame.Scene.SetCameraTransform(new(0, 2, 2), Quaternion.CreateFromYawPitchRoll(0, .4f, 0));
                }
                if (frame.Index is 1 or 4) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expectedBefore = image);
                if (frame.Index == 3) cpu.Lift.EditCpu(new HeightDeltaBrush(cpu.Lift.Grid.Range, .5f));
                if (frame.Index == 7) frame.Scene.CaptureImage(image => expectedPainted = image);
                if (frame.Index == 8)
                    foreach (TerrainDisplacementMaterial material in cpu.Materials) material.Displacement = 2f;
                if (frame.Index == 10) frame.Scene.CaptureImage(image => expectedDisplaced = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));
        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedPainted!, painted!);
        SceneImages.AssertEqual(expectedDisplaced!, displaced!);
        SceneImages.AssertChanged(before!, painted!);
        SceneImages.AssertChanged(painted!, displaced!);
    }
}
