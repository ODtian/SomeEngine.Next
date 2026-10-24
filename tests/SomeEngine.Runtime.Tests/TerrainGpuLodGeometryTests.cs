using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainGpuLodGeometryTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuLodAndDetailEditingMatchCpuPictures()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using GpuLodTerrainExample gpu = await GpuLodTerrainExample.CreateAsync(scheduler, content.DirectoryPath);
        var cpu = new LodTerrainExample(scheduler);
        SceneImage? near = null, far = null, expectedNear = null, expectedFar = null;
        TerrainChunk[] nearChunks = [], farChunks = [];
        TerrainEdit edit = default;
        TerrainGpuBuild build = default;
        int placement = 0;
        ApplicationResult? completed = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    var material = frame.Scene.GetMaterial(frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere"));
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    placement = gpu.Scene.Add(gpu.CreatePlacement(material));
                    frame.Scene.SetCameraTransform(new(-10, 4, 6), Quaternion.CreateFromYawPitchRoll(0, .45f, 0));
                }
                if (frame.Index is 1 or 11) gpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8)
                {
                    nearChunks = gpu.Scene.GetChunks(placement);
                    frame.Scene.CaptureImage(image => near = image);
                }
                if (frame.Index == 9)
                {
                    frame.Scene.SetCameraTransform(new(0, 10, -14), Quaternion.CreateFromYawPitchRoll(0, .33f, 0));
                    edit = gpu.RaiseDetail();
                }
                if (frame.Index == 10) edit.Completion.Complete();
                if (frame.Index == 17)
                {
                    farChunks = gpu.Scene.GetChunks(placement);
                    build = gpu.Scene.GetGpuBuild(gpu.Heights.Terrain);
                    frame.Scene.CaptureImage(image => far = image);
                }
            },
        };
        gpu.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.True(result == 0, completed?.Error?.ToString());
        Assert.Equal(new[] { 0, 1 }, nearChunks.Select(chunk => chunk.Lod).Distinct().Order());
        Assert.Contains(nearChunks, chunk => chunk.TransitionFaces != TerrainTransitionFaces.None);
        Assert.Equal(2, farChunks.Length);
        Assert.All(farChunks, chunk => { Assert.Equal(2, chunk.Lod); Assert.Equal(edit.Revision, chunk.Revision); });
        Assert.Equal(edit.Revision, build.Revision);
        Assert.True(build.Completion.IsCompleted);
        Assert.True(build.Visibility.IsCompleted);

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
                    cpu.Scene.Add(cpu.CreatePlacement(material));
                    frame.Scene.SetCameraTransform(new(-10, 4, 6), Quaternion.CreateFromYawPitchRoll(0, .45f, 0));
                }
                if (frame.Index is 1 or 10) cpu.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expectedNear = image);
                if (frame.Index == 9)
                {
                    frame.Scene.SetCameraTransform(new(0, 10, -14), Quaternion.CreateFromYawPitchRoll(0, .33f, 0));
                    cpu.RaiseDetail();
                }
                if (frame.Index == 17) frame.Scene.CaptureImage(image => expectedFar = image);
            },
        };
        cpu.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));
        SceneImages.AssertEqual(expectedNear!, near!);
        SceneImages.AssertEqual(expectedFar!, far!);
        SceneImages.AssertColorRegion(near!, 30, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertColorRegion(far!, 30, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertChanged(near!, far!);
    }
}
