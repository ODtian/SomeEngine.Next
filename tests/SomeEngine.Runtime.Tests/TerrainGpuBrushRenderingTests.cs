using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuBrushRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuHeightSmoothingAndFlatteningRenderTheCpuSurfaces()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        Shader shader = await TerrainGpuEditingTests.LoadNeighborhoodShaderAsync("terrain_height_neighborhood.slang");
        var gpu = new HeightTerrainExample(scheduler);
        var cpu = new HeightTerrainExample(scheduler);
        var impulse = new HeightDeltaBrush(new HeightRegion(2, 2, 1, 1), 3f);
        await gpu.Heights.EditCpu(impulse).WaitAsync();
        await cpu.Heights.EditCpu(impulse).WaitAsync();

        var actual = new SceneImage[3];
        var expected = new SceneImage[3];
        var center = new Vector2(2, 2);
        var plane = new Plane(Vector3.UnitY, -1f);
        TerrainEdit finalEdit = default;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "27"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    gpu.AddPlacements(red, green);
                }
                if (frame.Index == 1) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => actual[0] = image);
                if (frame.Index == 9) gpu.SmoothGpu(shader, center, 3f, 1);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => actual[1] = image);
                if (frame.Index == 18) finalEdit = gpu.FlattenGpu(shader, center, 3f, plane);
                if (frame.Index == 26) frame.Scene.CaptureImage(image => actual[2] = image);
            },
        };
        gpu.Install(application);
        Assert.Equal(0, await Application.RunAsync([], application: application));
        await finalEdit.WaitAsync();

        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "27"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var red = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    var green = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "green sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacements(red, green);
                }
                if (frame.Index is 1 or 10 or 19) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expected[0] = image);
                if (frame.Index == 9) cpu.Smooth(center, 3f, 1);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => expected[1] = image);
                if (frame.Index == 18) cpu.Flatten(center, 3f, plane);
                if (frame.Index == 26) frame.Scene.CaptureImage(image => expected[2] = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        HeightFieldRead heights = await gpu.Heights.ReadCpuAsync(gpu.Heights.Grid.Range);
        Assert.Equal(1f, heights[2, 2]);
        for (int index = 0; index < actual.Length; index++) SceneImages.AssertEqual(expected[index], actual[index]);
        SceneImages.AssertChanged(actual[0], actual[1]);
        SceneImages.AssertChanged(actual[1], actual[2]);
    }

    [Fact]
    public async Task GpuVoxelSmoothingAndFlatteningRenderTheCpuSurfaces()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        Shader shader = await TerrainGpuEditingTests.LoadNeighborhoodShaderAsync("terrain_voxel_neighborhood.slang");
        var gpu = new VoxelTerrainExample(scheduler);
        var cpu = new VoxelTerrainExample(scheduler);

        var actual = new SceneImage[3];
        var expected = new SceneImage[3];
        var center = new Vector3(-1, 0, 0);
        var plane = new Plane(Vector3.UnitX, 1f);
        TerrainEdit finalEdit = default;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "27"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    gpu.AddPlacement(material);
                }
                if (frame.Index == 1) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => actual[0] = image);
                if (frame.Index == 9) gpu.SmoothGpu(shader, center, 2f, 1, .25f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => actual[1] = image);
                if (frame.Index == 18) finalEdit = gpu.FlattenGpu(shader, center, 2f, plane);
                if (frame.Index == 26) frame.Scene.CaptureImage(image => actual[2] = image);
            },
        };
        gpu.Install(application);
        Assert.Equal(0, await Application.RunAsync([], application: application));
        await finalEdit.WaitAsync();

        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "27"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    cpu.AddPlacement(material);
                }
                if (frame.Index is 1 or 10 or 19) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expected[0] = image);
                if (frame.Index == 9) cpu.Smooth(center, 2f, 1, .25f);
                if (frame.Index == 17) frame.Scene.CaptureImage(image => expected[1] = image);
                if (frame.Index == 18) cpu.Flatten(center, 2f, plane);
                if (frame.Index == 26) frame.Scene.CaptureImage(image => expected[2] = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        VoxelFieldRead values = await gpu.Field.ReadCpuAsync(gpu.SampleRegion);
        Assert.Equal(0f, values[-1, 0, 0]);
        for (int index = 0; index < actual.Length; index++) SceneImages.AssertEqual(expected[index], actual[index]);
        SceneImages.AssertChanged(actual[0], actual[1]);
        SceneImages.AssertChanged(actual[1], actual[2]);
    }
}
