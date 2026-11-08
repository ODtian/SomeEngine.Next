using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class LightEncodingRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task ChangedAndRemovedLightTypesMatchStartingWithTheFinalLights()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-LightEncoding-{Guid.NewGuid():N}");
        try
        {
            await SomeEngine.SceneContent.BehaviorScenes.CreateShadowReceiverAsync(content.DirectoryPath, directory);
            SceneImage? before = null, changed = null, expected = null;
            Entity point = default, spot = default, directional = default;
            var scene = new ApplicationScene(directory)
            {
                Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]),
                Initialize = frame =>
                {
                    point = frame.Scene.PointLights.Single();
                    directional = frame.World.CreateEntity(new DirectionalLight(new(0, 0, 2), Vector3.One, 1));
                    spot = frame.World.CreateEntity(new SpotLight(new(-2, 2, 3), 20, new(2, -2, 5), .95f, .7f,
                        new(0, 1, 0), 30));
                },
                UpdateFrame = frame =>
                {
                    if (frame.Index == 1) frame.Scene.CaptureImage(image => before = image);
                    if (frame.Index == 2)
                    {
                        frame.World.DestroyEntity(directional);
                        SetFinalLights(frame, point, spot);
                    }
                    if (frame.Index == 3) frame.Scene.CaptureImage(image => changed = image);
                },
            };
            ApplicationResult result = await Application.RunAsync([], scene);
            Assert.True(result.Succeeded, result.Error?.ToString());
            var reference = new ApplicationScene(directory)
            {
                Options = scene.Options,
                Initialize = frame =>
                {
                    Entity target = frame.World.CreateEntity(FinalSpot());
                    SetFinalLights(frame, frame.Scene.PointLights.Single(), target);
                },
                UpdateFrame = frame => { if (frame.Index == 3) frame.Scene.CaptureImage(image => expected = image); },
            };
            ApplicationResult referenceResult = await Application.RunAsync([], reference);
            Assert.True(referenceResult.Succeeded, referenceResult.Error?.ToString());
            Assert.NotEqual(before!.Pixels.ToArray(), changed!.Pixels.ToArray());
            SceneImages.AssertEqual(expected!, changed);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static SpotLight FinalSpot() => new(new(2, 2, 3), 20, new(-2, -2, 5), .95f, .7f, new(1, .2f, .1f), 20);

    private static void SetFinalLights(ApplicationFrame frame, Entity point, Entity spot)
    {
        frame.World.Replace(point, new PointLight(new(-4, 2, 3), 20, new(.2f, .4f, 1), 30));
        frame.World.Replace(spot, FinalSpot());
    }
}
