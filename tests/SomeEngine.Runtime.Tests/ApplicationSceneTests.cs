using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render;
using SomeEngine.Render.Features.Layers;
using SomeEngine.Render.Features.Decals;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationSceneTests(SceneContentFixture content)
{
    [Fact]
    public async Task FrameActionsCaptureSelectedFrames()
    {
        SceneImage? captured = null;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = ApplicationOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captured = image);
            },
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Frame);
        Assert.Equal(64 * 64 * 4, captured.Pixels.Length);
    }

    [Fact]
    public async Task ReloadRecreatesRenderingForDeviceSettings()
    {
        var frames = new List<int>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = ApplicationOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame => frames.Add(frame.Index),
        };
        scene.FrameCompleted = frame =>
        {
            if (frame.Index != 1) return;
            scene.Options = scene.Options with { FrameLimit = 1, DisableMeshShaders = true };
            scene.Reload();
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.Equal([0, 1, 0], frames);
    }
    [Fact]
    public async Task LoadingScenesKeepsFrameIndicesAndSwitchesCameraAndWorldContent()
    {
        string secondDirectory = Path.Combine(Path.GetTempPath(), "SomeEngine-scene-switch-" + Guid.NewGuid().ToString("N"));
        DirectoryInfo engine = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(engine.FullName, "SomeEngine.slnx"))) engine = engine.Parent!;
        Vector3 secondCamera = new(2, 1, 0);
        try
        {
            var sourceManifest = AssetManifest.Load(Path.Combine(content.DirectoryPath, "Library", "AssetManifest"));
            await using var files = EngineShaderContent.OpenFiles(content.DirectoryPath, sourceManifest);
            AssetEntry entry = sourceManifest.List(ApplicationSettingsAsset.FileType).Single();
            using var application = await ApplicationSettingsAsset.OpenAsync(files, new(entry.AssetGuid));
            var settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
            using var sceneAsset = await WorldAsset.OpenAsync(files, settings.Scene);
            using var secondWorld = await sceneAsset.LoadAsync(SceneSerialization.CreateRegistry());
            var cameraQuery = secondWorld.Query(new QueryDefinitionBuilder().Read<SceneCamera>());
            SomeEngine.ECS.Entities.Entity cameraEntity = default;
            secondWorld.ExecuteQuery(cameraQuery, rows =>
            {
                foreach (var row in rows.Rows) { cameraEntity = row.Entity; break; }
            });
            SceneCamera camera = secondWorld.Read<SceneCamera>(cameraEntity);
            camera.Position = secondCamera;
            secondWorld.Replace(cameraEntity, camera);
            secondWorld.ReleaseQuery(cameraQuery);
            var meshQuery = secondWorld.Query(new QueryDefinitionBuilder().Read<MeshSource>());
            SomeEngine.ECS.Entities.Entity removed = default;
            secondWorld.ExecuteQuery(meshQuery, rows =>
            {
                foreach (var row in rows.Rows) { removed = row.Entity; break; }
            });
            secondWorld.ReleaseQuery(meshQuery);
            secondWorld.DestroyEntity(removed);
            sceneAsset.WriteWorld(secondWorld, SceneSerialization.CreateRegistry());
            var secondProject = new AssetProject(secondDirectory, []);
            EngineShaderContent.SetContentDirectories(secondProject,
                [content.DirectoryPath, Path.Combine(engine.FullName, "samples", "DefaultGame"), engine.FullName]);
            secondProject.RegisterAsset(await sceneAsset.SaveAsync(Path.Combine(secondDirectory, sourceManifest.Assets[sceneAsset.Id].Path)));
            secondProject.RegisterAsset(await application.SaveAsync(Path.Combine(secondDirectory, entry.Path)));

            var indices = new List<int>();
            var worlds = new List<World>();
            var frameCounts = new List<int>();
            var meshes = new List<(int Scene, int Count)>();
            var cameras = new List<(int Scene, Vector3 Position)>();
            var graphs = new List<global::SomeEngine.RenderGraph.RenderGraph>();
            var images = new List<SceneImage>();
            var shadows = new List<bool>();
            var stepSeconds = new List<double>();
            LayerComposition? layers = null;
            Exception? sceneError = null;
            var layerPassCounts = new List<int>();
            var scene = new ApplicationScene(content.DirectoryPath)
            {
                Options = ApplicationOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--fixed-step", "0.016666667", "--frames", "120"]),
                CreateFrameFeatures = rendering =>
                {
                    graphs.Add(rendering.Graph);
                    layers = rendering.Layers;
                    layers!.AddPass(new("Scene layer"));
                    return [];
                },
                SceneCompleted = result => sceneError = result.Error,
            };
            scene.UpdateFrame = frame =>
            {
                if (worlds.Count == 0 || !ReferenceEquals(worlds[^1], frame.World))
                {
                    worlds.Add(frame.World);
                    frameCounts.Add(0);
                }
                int current = worlds.Count - 1;
                indices.Add(frame.Index);
                stepSeconds.Add(frame.DeltaSeconds);
                frameCounts[current]++;
                meshes.Add((current, frame.Scene.MeshEntities.Count));
                Matrix4x4.Invert(frame.Scene.CameraView, out Matrix4x4 cameraWorld);
                cameras.Add((current, new(cameraWorld.M41, cameraWorld.M42, cameraWorld.M43)));
                if (frameCounts[current] == 2)
                {
                    frame.Scene.CaptureLinearImage(image => shadows.Add(image.Frame.ShadowSettings.Enabled));
                    if (current is 0 or 2) frame.Scene.CaptureImage(images.Add);
                    if (current < 2)
                    {
                        scene.Options = scene.Options with
                        {
                            VirtualShadows = current == 0,
                        };
                        scene.LoadScene(current == 0 ? secondDirectory : content.DirectoryPath);
                    }
                    else scene.Options = scene.Options with { FrameLimit = frame.Index + 1 };
                }
            };
            scene.FrameCompleted = _ => layerPassCounts.Add(layers!.Passes.Length);

            int result = await Application.RunAsync([], application: scene);
            Assert.True(result == 0, sceneError?.ToString());
            Assert.Equal(3, worlds.Count);
            Assert.All(frameCounts, count => Assert.True(count >= 2));
            Assert.Equal(Enumerable.Range(indices[0], indices.Count), indices);
            Assert.All(stepSeconds, step => Assert.Equal(0.016666667, step, 8));
            Assert.All(meshes, value => Assert.Equal(value.Scene == 1 ? 1 : 2, value.Count));
            Assert.All(cameras, value => Assert.True(Vector3.Distance(
                value.Scene == 1 ? secondCamera : Vector3.Zero, value.Position) < .0001f));
            Assert.Single(graphs);
            Assert.Equal([false, true, false], shadows);
            Assert.All(layerPassCounts, count => Assert.Equal(1, count));
            Assert.Equal(2, images.Count);
            SceneImages.AssertEqualRegion(images[0], images[1], 16, 16, 32, 32);
        }
        finally { if (Directory.Exists(secondDirectory)) Directory.Delete(secondDirectory, true); }
    }
}
