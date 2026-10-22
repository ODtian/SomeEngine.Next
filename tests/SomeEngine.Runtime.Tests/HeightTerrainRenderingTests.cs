using System.Numerics;
using DefaultGame;
using SomeEngine.Core.Math;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class HeightTerrainRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task HeightEditMovesBothSharedPlacementsToTheRaisedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new HeightTerrainExample(scheduler);
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
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
                if (frame.Index == 9) example.Heights.EditCpu(new HeightDeltaBrush(example.Heights.Grid.Range, 1f));
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.Equal(0, result);

        Mesh ground = CreateGround(0);
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 18,
        [
            new(0, scene =>
            {
                var red = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var green = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(ground, [red], new TransformQvvs(new Vector3(-5, -2, 6), Quaternion.Identity));
                scene.AddEntity(ground, [green], new TransformQvvs(new Vector3(1, -2, 6), Quaternion.Identity));
            }),
            new(8, scene => scene.CaptureImage(image => expectedBefore = image)),
            new(9, _ => ground.Update(CreateGround(1))),
            new(17, scene => scene.CaptureImage(image => expectedAfter = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);

        static Mesh CreateGround(float height)
        {
            Vector3[] positions = [new(0, height, 0), new(4, height, 0), new(0, height, 4), new(4, height, 4)];
            StandardClusterAttributes[] attributes = positions.Select(position =>
                new StandardClusterAttributes(Vector3.UnitY, new(1, 0, 0, 1), new(position.X, position.Z))).ToArray();
            return ClusterMeshBuilder.Build(positions, attributes, [0u, 2, 1, 1, 2, 3], [new(0, 6, 0)],
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Raised ground reference");
        }
    }

}
