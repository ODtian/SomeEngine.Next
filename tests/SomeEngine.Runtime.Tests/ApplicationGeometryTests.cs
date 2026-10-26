using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using SomeEngine.Assets.Schema;
using ClusterGeometry = SomeEngine.Render.Cluster.ClusterGeometry;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationGeometryTests(SceneContentFixture content)
{
    [Fact]
    public async Task RepeatedMeshReplacementReusesTheGeometryBudget()
    {
        using MeshAsset asset = await MeshAsset.OpenAsync(Path.Combine(content.DirectoryPath, "assets/Meshes/sphere-0.mesh.asset"));
        ClusterGeometryBlock geometry = await asset.GetBlockAsync<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = await asset.GetBlockAsync<ClusterVertexAttributesBlock>();
        using var source = await geometry.OpenPayloadSourceAsync(attributes);
        SceneImage? before = null, after = null;
        Mesh? mesh = null;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            GeometryBvhMemoryBytes = checked((ulong)source.BvhLength * 3),
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "20"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0) mesh = frame.Scene.GetMesh(frame.Scene.MeshEntities[0]);
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index is 9 or 11) mesh!.Update(mesh.Duplicate());
                if (frame.Index == 19) frame.Scene.CaptureImage(image => after = image);
            },
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        SceneImages.AssertEqual(before!, after!);
    }

    [Fact]
    public async Task ReplacingMeshDataSwitchesGeometryAndPreservesThePicture()
    {
        SceneImage? before = null, after = null;
        Mesh? mesh = null;
        var inspection = new GeometryInspection();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "18"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    inspection.Source = frame.Scene.MeshEntities[0];
                    mesh = frame.Scene.GetMesh(inspection.Source);
                }
                if (frame.Index == 8) frame.Scene.CaptureImage(image => before = image);
                if (frame.Index == 9) mesh!.Update(mesh.Duplicate());
                if (frame.Index == 17) frame.Scene.CaptureImage(image => after = image);
            },
        };
        scene.InstallGeometry(inspection);

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.Equal(2, inspection.Geometries.Count);
        ClusterGeometry first = inspection.Geometries[0], second = inspection.Geometries[1];
        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.Equal(first.VertexEncoding, second.VertexEncoding);
        Assert.Equal(first.Bounds, second.Bounds);
        Assert.NotEqual(first.Allocation, second.Allocation);
        SceneImages.AssertEqual(before!, after!);
    }

    private sealed class GeometryInspection : IApplicationGeometryFeature, ISystem<RenderFrameSystemContext>
    {
        private QueryHandle _query;
        public Entity Source { get; set; }
        public List<ClusterGeometry> Geometries { get; } = [];

        public IDisposable? Install(ApplicationGeometryContext context)
        {
            context.AddFrameSystem(this);
            return null;
        }

        public void OnCreate(ref RenderFrameSystemContext context) =>
            _query = context.World.Query(new QueryDefinitionBuilder().Read<RenderSource>().Read<RenderClusterGeometry>());

        public void OnUpdate(ref RenderFrameSystemContext context) =>
            context.World.ExecuteQuery(_query, query =>
            {
                foreach (QueryChunkView chunk in query.Chunks)
                {
                    ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
                    ReadOnlySpan<RenderClusterGeometry> geometries = chunk.Read<RenderClusterGeometry>();
                    for (int row = 0; row < sources.Length; row++)
                    {
                        if (sources[row].Entity != Source) continue;
                        ClusterGeometry geometry = geometries[row].Geometry;
                        if (Geometries.Count == 0 || Geometries[^1].Revision != geometry.Revision)
                            Geometries.Add(geometry);
                    }
                }
            });

        public void OnDestroy(ref RenderFrameSystemContext context) => context.World.ReleaseQuery(_query);
    }
}
