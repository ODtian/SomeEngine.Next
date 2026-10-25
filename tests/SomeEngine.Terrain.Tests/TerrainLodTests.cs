using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainLodTests
{
    [Fact]
    public async Task CameraAndFieldChangesReplaceTheSameTerrainRegionAsACompletedBatch()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var material = new UnlitMaterial();
        using var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(33, 17, 4), .5f);
        terrain.Surface = new HeightSurface(heights);
        var scene = new TerrainScene(new TerrainClusterAdapter<StandardClusterAttributes>(new SurfaceAttributes(),
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime), new(2, 1f));
        int placement = scene.Add(new TerrainPlacement(terrain, new(0, 0, 0, 2, 1, 1), [material],
            new TerrainLodDistances(3, 6, 12))
        {
            Transform = new TransformQvvs(new(20, 0, 0), Quaternion.Identity),
        });
        Vector3 camera = new(21, 1, 1);
        systems.Add(new TerrainCommitSystem(scene));
        systems.Add(new TerrainLodSystem(scene, () => camera));
        systems.Add(new TerrainMeshSystem(scene));
        systems.Update();
        await scene.WaitForBuildsAsync();
        systems.Update();
        TerrainChunk[] before = scene.GetChunks(placement);
        Assert.Equal(new[] { 0, 1, 2, 3 }, before.Select(chunk => chunk.Lod).Distinct().Order());
        Assert.Contains(before, chunk => chunk.TransitionFaces != TerrainTransitionFaces.None);
        Assert.Equal(8192f, before.Sum(chunk => MathF.Pow(scene.Grid.GetBounds(chunk.Coordinates, chunk.Lod).Max.X
            - scene.Grid.GetBounds(chunk.Coordinates, chunk.Lod).Min.X, 3)));
        for (int first = 0; first < before.Length; first++)
        for (int second = first + 1; second < before.Length; second++)
        {
            TerrainBounds a = scene.Grid.GetBounds(before[first].Coordinates, before[first].Lod).Volume;
            TerrainBounds b = scene.Grid.GetBounds(before[second].Coordinates, before[second].Lod).Volume;
            Vector3 overlap = Vector3.Min(a.Max, b.Max) - Vector3.Max(a.Min, b.Min);
            Assert.False(overlap.X > 0 && overlap.Y > 0 && overlap.Z > 0);
            if (overlap.X == 0 && overlap.Y > 0 && overlap.Z > 0
                || overlap.Y == 0 && overlap.X > 0 && overlap.Z > 0
                || overlap.Z == 0 && overlap.X > 0 && overlap.Y > 0)
                Assert.InRange(Math.Abs(before[first].Lod - before[second].Lod), 0, 1);
        }

        var input = new InputReady();
        JobHandle dependency = scheduler.CreateExternalFenceHandle(input);
        TerrainEdit edit = heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1), dependency);
        camera = new(100);
        try
        {
            systems.Update();
            Assert.Equal(before.OrderBy(chunk => chunk.GeometryId), scene.GetChunks(placement).OrderBy(chunk => chunk.GeometryId));
        }
        finally { input.Complete(); }
        await scene.WaitForBuildsAsync();
        systems.Update();
        TerrainChunk[] after = scene.GetChunks(placement);
        Assert.Equal(2, after.Length);
        Assert.All(after, chunk =>
        {
            Assert.Equal(3, chunk.Lod);
            Assert.Equal(edit.Revision, chunk.Revision);
            Assert.Equal(TerrainTransitionFaces.None, chunk.TransitionFaces);
            Assert.NotNull(scene.GetGeometry(chunk.GeometryId));
        });
    }

    [Fact]
    public async Task CoarseAndFineSphereChunksFormOneClosedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("sphere", new VoxelGrid(4));
        field.EditCpu(new SphereSamples(new(-1, -1, -1, 19, 11, 11)));
        terrain.Surface = new VoxelSurface(field);
        TerrainSurfaceRead surface = await terrain.Surface.ReadCpuAsync(new(new(-1), new(17, 9, 9)));
        var mesher = new MarchingCubesTerrainMesher();
        var meshes = new List<TerrainSurfaceMesh>
        {
            mesher.Build(surface, new(new(0, 0, 0), 4, 2f, 1, TerrainTransitionFaces.PositiveX)),
        };
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 2; x < 4; x++)
            meshes.Add(mesher.Build(surface, new(new(x, y, z), 4, 1f)));

        var edges = new Dictionary<(Point, Point), (int Count, int Direction)>();
        foreach (TerrainSurfaceMesh mesh in meshes)
        {
            ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
            ReadOnlySpan<uint> indices = mesh.Indices.Span;
            for (int index = 0; index < indices.Length; index += 3)
            {
                Vector3 a = positions[(int)indices[index]], b = positions[(int)indices[index + 1]],
                    c = positions[(int)indices[index + 2]];
                AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
            }
        }
        Assert.NotEmpty(edges);
        Assert.All(edges.Values, edge => Assert.Equal((2, 0), edge));

        void AddEdge(Vector3 a, Vector3 b)
        {
            Point first = Point.From(a), second = Point.From(b);
            int direction = first.CompareTo(second) < 0 ? 1 : -1;
            var key = direction > 0 ? (first, second) : (second, first);
            var value = edges.GetValueOrDefault(key);
            edges[key] = (value.Count + 1, value.Direction + direction);
        }
    }

    private readonly record struct Point(int X, int Y, int Z) : IComparable<Point>
    {
        public static Point From(Vector3 value) => new((int)MathF.Round(value.X * 100000),
            (int)MathF.Round(value.Y * 100000), (int)MathF.Round(value.Z * 100000));
        public int CompareTo(Point other)
        {
            int result = X.CompareTo(other.X);
            if (result == 0) result = Y.CompareTo(other.Y);
            return result == 0 ? Z.CompareTo(other.Z) : result;
        }
    }

    private readonly record struct SphereSamples(VoxelRegion Range) : IVoxelBrushCpu
    {
        public int ReadBorder => 0;
        public float Apply(VoxelFieldRead source, int x, int y, int z) =>
            Vector3.Distance(new(x, y, z), new(8, 4, 4)) - 2.5f;
    }

    private sealed class InputReady : IJobExternalFence
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => _completion.Task.IsCompleted;
        public void OnSignaled(Action<object?> continuation, object? state)
        {
            if (IsSignaled) continuation(state);
            else _completion.Task.GetAwaiter().OnCompleted(() => continuation(state));
        }
        public void Complete() => _completion.SetResult();
    }

    private sealed class SurfaceAttributes : ITerrainAttributeGenerator<StandardClusterAttributes>
    {
        public int ReadBorder => 0;
        public StandardClusterAttributes CreateVertex(TerrainSurfaceRead surface, TerrainSurfacePoint point) =>
            new(point.Normal, new(1, 0, 0, 1), new(point.SamplePosition.X, point.SamplePosition.Z));
        public byte GetMaterial(TerrainSurfaceRead surface, Vector3 position) => 0;
    }
}
