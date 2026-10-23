using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

namespace SomeEngine.Terrain.Tests;

public sealed class TerrainLodTests
{
    [Fact]
    public async Task CoarseAndFineSphereChunksFormOneClosedSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var terrain = new Resources.Terrain(new TerrainEditing(scheduler));
        VoxelField field = terrain.CreateVoxelField("sphere", new VoxelGrid(4));
        field.EditCpu(new SphereSamples(new(-1, -1, -1, 11, 7, 7)));
        terrain.Surface = new VoxelSurface(field);
        TerrainSurfaceRead surface = await terrain.Surface.ReadCpuAsync(new(new(-1), new(9, 5, 5)));
        var mesher = new MarchingCubesTerrainMesher();
        var meshes = new List<TerrainSurfaceMesh>
        {
            mesher.Build(surface, new(new(0, 0, 0), 2, 2f, 1, TerrainTransitionFaces.PositiveX)),
        };
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 2; x < 4; x++)
            meshes.Add(mesher.Build(surface, new(new(x, y, z), 2, 1f)));

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
            Vector3.Distance(new(x, y, z), new(4, 2, 2)) - 1.6f;
    }
}
