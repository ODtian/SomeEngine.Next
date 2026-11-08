using System.Numerics;
using DefaultGame;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ProceduralClusterRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task RealtimeAndOfflineGeometryDisplayTheSameMaterialRegions()
    {
        SceneImage? realtime = null, offline = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                ProceduralClusterExample.Add(scene, left, right);
            }),
            new(9, scene => scene.CaptureImage(image => realtime = image)),
        ]), "--no-taa", "--no-vsm");

        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(ProceduralClusterExample.CreateMesh(ClusterBuildStrategy.Offline), [left, right],
                    new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(9, scene => scene.CaptureImage(image => offline = image)),
        ]), "--no-taa", "--no-vsm");

        SceneImages.AssertColorRegion(realtime!, 20, 30, 4, 4, 0, 186, 0);
        SceneImages.AssertColorRegion(realtime!, 40, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertEqual(offline!, realtime!);
    }

    [Fact]
    public async Task ParallelLodBuildDisplaysTheSameSurfaceAsOneExecutionThread()
    {
        var input = CreateGrid();
        Mesh[] meshes = new Mesh[2];
        using (var singleThread = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 }))
            singleThread.Schedule(new BuildGrid(input.Positions, input.Attributes, input.Indices, meshes, 0)).Complete();
        using (var workers = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 }))
            workers.Schedule(new BuildGrid(input.Positions, input.Attributes, input.Indices, meshes, 1)).Complete();
        SceneImage? reference = null, parallel = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(meshes[0], [left, right], new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(9, scene => scene.CaptureImage(image => reference = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 10,
        [
            new(0, scene =>
            {
                var left = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "red sphere"));
                var right = scene.GetMaterial(scene.MeshEntities.Single(entity => scene.GetMaterial(entity).Name == "green sphere"));
                foreach (var entity in scene.MeshEntities) scene.DeleteEntity(entity);
                scene.AddEntity(meshes[1], [left, right], new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(9, scene => scene.CaptureImage(image => parallel = image)),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertColorRegion(reference!, 20, 30, 4, 4, 0, 186, 0);
        SceneImages.AssertColorRegion(reference!, 40, 30, 4, 4, 186, 0, 0);
        SceneImages.AssertEqual(reference!, parallel!);
    }

    private readonly record struct BuildGrid(Vector3[] Positions, StandardClusterAttributes[] Attributes,
        uint[] Indices, Mesh[] Results, int Index) : IJob
    {
        public void Execute()
        {
            int regionIndices = Indices.Length / 2;
            Results[Index] = ClusterMeshBuilder.Build(Positions, Attributes, Indices,
                [new ClusterMaterialGroup(0, regionIndices, 0), new(regionIndices, regionIndices, 1)],
                StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Offline, "Subdivided material regions");
        }
    }

    private static (Vector3[] Positions, StandardClusterAttributes[] Attributes, uint[] Indices) CreateGrid()
    {
        const int divisions = 32;
        List<Vector3> positions = [];
        List<StandardClusterAttributes> attributes = [];
        List<uint> indices = [];
        for (int region = 0; region < 2; region++)
        {
            uint vertexOffset = (uint)positions.Count;
            for (int y = 0; y <= divisions; y++)
            for (int x = 0; x <= divisions; x++)
            {
                Vector3 position = new(-2 + region * 2 + x * 2f / divisions, -1 + y * 2f / divisions, 0);
                positions.Add(position);
                attributes.Add(new(-Vector3.UnitZ, new(1, 0, 0, 1),
                    new((position.X + 2) / 4, (position.Y + 1) / 2)));
            }
            for (int y = 0; y < divisions; y++)
            for (int x = 0; x < divisions; x++)
            {
                uint a = vertexOffset + (uint)(y * (divisions + 1) + x);
                uint b = a + 1;
                uint c = a + divisions + 1;
                uint d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        }
        return (positions.ToArray(), attributes.ToArray(), indices.ToArray());
    }
}
