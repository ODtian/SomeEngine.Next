using System.Numerics;
using DefaultGame;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class TerrainLodRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task CameraMovementAndDetailEditingReachTheFreshCoarseScenePicture()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new LodTerrainExample(scheduler);
        SceneImage? before = null, after = null, expected = null;
        TerrainChunk[] nearChunks = [], farChunks = [];
        TerrainEdit edit = default;
        int placement = 0;
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
                    placement = example.Scene.Add(example.CreatePlacement(material));
                    frame.Scene.SetCameraTransform(new(-10, 4, 6), Quaternion.CreateFromYawPitchRoll(0, .45f, 0));
                }
                if (frame.Index is 1 or 10) example.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8)
                {
                    nearChunks = example.Scene.GetChunks(placement);
                    frame.Scene.CaptureImage(image => before = image);
                }
                if (frame.Index == 9)
                {
                    frame.Scene.SetCameraTransform(new(0, 10, -14), Quaternion.CreateFromYawPitchRoll(0, .33f, 0));
                    edit = example.RaiseDetail();
                }
                if (frame.Index == 17)
                {
                    farChunks = example.Scene.GetChunks(placement);
                    frame.Scene.CaptureImage(image => after = image);
                }
            },
        };
        example.Install(application);
        Assert.Equal(0, await Application.RunAsync([], application: application));
        Assert.Equal(new[] { 0, 1 }, nearChunks.Select(chunk => chunk.Lod).Distinct().Order());
        Assert.Contains(nearChunks, chunk => chunk.TransitionFaces != TerrainTransitionFaces.None);
        Assert.Equal(2, farChunks.Length);
        Assert.All(farChunks, chunk => { Assert.Equal(2, chunk.Lod); Assert.Equal(edit.Revision, chunk.Revision); });

        var fresh = new LodTerrainExample(scheduler);
        fresh.RaiseDetail();
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
                    fresh.Scene.Add(fresh.CreatePlacement(material));
                    frame.Scene.SetCameraTransform(new(0, 10, -14), Quaternion.CreateFromYawPitchRoll(0, .33f, 0));
                }
                if (frame.Index == 1) fresh.Scene.WaitForBuildsAsync().GetAwaiter().GetResult();
                if (frame.Index == 8) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        fresh.Install(reference);
        Assert.Equal(0, await Application.RunAsync([], application: reference));
        SceneImages.AssertEqual(expected!, after!);
        SceneImages.AssertColorRegion(before!, 30, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertColorRegion(after!, 30, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertChanged(before!, after!);
    }
}
