using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainBrushRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task HeightSmoothingChangesSamplesAndTheRenderedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new HeightTerrainExample(scheduler);
        await example.Heights.EditCpu(new HeightDeltaBrush(new HeightRegion(2, 2, 1, 1), 3f)).WaitAsync();
        HeightFieldRead initial = await example.Heights.ReadCpuAsync(example.Heights.Grid.Range);
        SceneImage? before = null, after = null;
        TerrainEdit edit = default;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    example.AddPlacements(red, green);
                }
                if (frame.Index is 1 or 10) example.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) edit = example.Smooth(new Vector2(2, 2), 3f, 1);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);

        Assert.Equal(0, await Application.RunAsync([], application: application));
        HeightFieldRead current = await example.Heights.ReadCpuAsync(example.Heights.Grid.Range);
        Assert.Equal(3f, initial[2, 2]);
        Assert.Equal(.33333334f, current[2, 2], tolerance: 1e-6f);
        Assert.Equal(edit.Revision, current.Revision);
        SceneImages.AssertChanged(before!, after!);
    }

    [Fact]
    public async Task VoxelFlatteningChangesSamplesAndTheRenderedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new VoxelTerrainExample(scheduler);
        VoxelFieldRead initial = await example.Field.ReadCpuAsync(example.SampleRegion);
        SceneImage? before = null, after = null;
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
                if (frame.Index == 9) edit = example.Flatten(new Vector3(-1, 0, 0), 2f, new Plane(Vector3.UnitX, 1f));
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);

        Assert.Equal(0, await Application.RunAsync([], application: application));
        VoxelFieldRead current = await example.Field.ReadCpuAsync(example.SampleRegion);
        Assert.Equal(-1f, initial[-1, 0, 0]);
        Assert.Equal(0f, current[-1, 0, 0]);
        Assert.Equal(-.5f, current[-2, 0, 0]);
        Assert.Equal(.5f, current[0, 0, 0]);
        Assert.Equal(edit.Revision, current.Revision);
        SceneImages.AssertChanged(before!, after!);
    }
}
