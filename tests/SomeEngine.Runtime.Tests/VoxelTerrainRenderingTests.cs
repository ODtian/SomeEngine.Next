using System.Numerics;
using DefaultGame;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class VoxelTerrainRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task VoxelEditMovesTheSampledSurfaceAndItsRenderedGeometry()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var example = new VoxelTerrainExample(scheduler);
        VoxelFieldRead earlier = await example.Field.ReadCpuAsync(example.SampleRegion);
        SceneImage? before = null, after = null, expectedBefore = null, expectedAfter = null;
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
                if (frame.Index == 9) edit = example.WriteSphere(new Vector3(1, 0, 0));
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        example.Install(application);
        int result = await Application.RunAsync([], application: application);
        Assert.Equal(0, result);
        VoxelFieldRead current = await example.Field.ReadCpuAsync(example.SampleRegion);
        Assert.Equal(-1f, earlier[-1, 0, 0]);
        Assert.Equal(1f, current[-1, 0, 0]);
        Assert.Equal(-1f, current[1, 0, 0]);
        Assert.Equal(edit.Revision, current.Revision);

        // The sampled unit sphere has six axis-aligned zero crossings and eight triangular faces.
        Vector3[] positions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        StandardClusterAttributes[] attributes = positions.Select(position =>
        {
            Vector3 tangent = MathF.Abs(position.X) > .5f ? Vector3.UnitY : Vector3.UnitX;
            return new StandardClusterAttributes(position, new(tangent, 1), new(position.X, position.Z));
        }).ToArray();
        uint[] indices = [2, 4, 0, 2, 1, 4, 2, 5, 1, 2, 0, 5, 3, 0, 4, 3, 4, 1, 3, 1, 5, 3, 5, 0];
        Mesh reference = ClusterMeshBuilder.Build(positions, attributes, indices, [new(0, 24, 0)],
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Sampled sphere reference");
        Entity sphere = default;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 18,
        [
            new(0, scene =>
            {
                var material = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                sphere = scene.AddEntity(reference, [material], new TransformQvvs(new Vector3(-1, 0, 6), Quaternion.Identity));
            }),
            new(8, scene => scene.CaptureImage(image => expectedBefore = image)),
            new(9, scene => scene.SetEntityTransform(sphere, new TransformQvvs(new Vector3(1, 0, 6), Quaternion.Identity))),
            new(17, scene => scene.CaptureImage(image => expectedAfter = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertEqual(expectedBefore!, before!);
        SceneImages.AssertEqual(expectedAfter!, after!);
    }
}
