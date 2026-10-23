using System.Numerics;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Paired Marching Cubes and Transvoxel cells, with negative samples inside the surface.</summary>
public sealed partial class MarchingCubesTerrainMesher : ITerrainMesher
{
    public int ReadBorder => 1;

    public TerrainSurfaceMesh Build(TerrainSurfaceRead surface, TerrainChunkBounds bounds)
    {
        var positions = new List<Vector3>();
        var samples = new List<TerrainSurfacePoint>();
        var indices = new List<uint>();
        var edges = new Dictionary<GridEdge, uint>();
        Span<float> values = stackalloc float[8];
        Span<uint> vertices = stackalloc uint[12];

        for (int z = 0; z < bounds.CellCount; z++)
        for (int x = 0; x < bounds.CellCount; x++)
        {
            int minY = 0, maxY = bounds.CellCount;
            var column = new TerrainBounds(bounds.SamplePosition(x, 0, z),
                bounds.SamplePosition(x + 1, bounds.CellCount, z + 1));
            if (surface.GetHeightRange(column) is { } heights)
            {
                minY = Math.Clamp((int)MathF.Floor((heights.Min - bounds.Min.Y) / bounds.CellSize) - 1, 0, bounds.CellCount);
                maxY = Math.Clamp((int)MathF.Ceiling((heights.Max - bounds.Min.Y) / bounds.CellSize) + 1, 0, bounds.CellCount);
            }
            for (int y = minY; y < maxY; y++)
            {
                int cellCase = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 position = bounds.SamplePosition(x + (corner & 1), y + ((corner >> 1) & 1),
                        z + ((corner >> 2) & 1));
                    values[corner] = surface.Sample(position);
                    if (values[corner] < 0) cellCase |= 1 << corner;
                }

                int cellClass = TransvoxelTables.RegularClasses[cellCase];
                int vertexCount = TransvoxelTables.RegularCounts[cellClass] >> 4;
                ReadOnlySpan<ushort> vertexEdges = TransvoxelTables.RegularEdges.Slice(cellCase * 12, vertexCount);
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int a = (vertexEdges[vertex] >> 4) & 15, b = vertexEdges[vertex] & 15;
                    var edge = new GridEdge(x + (a & 1), y + ((a >> 1) & 1), z + ((a >> 2) & 1), a ^ b);
                    if (!edges.TryGetValue(edge, out uint index))
                    {
                        Vector3 start = bounds.SamplePosition(edge.X, edge.Y, edge.Z);
                        Vector3 end = bounds.SamplePosition(x + (b & 1), y + ((b >> 1) & 1), z + ((b >> 2) & 1));
                        Vector3 position = FindIntersection(surface, start, end, values[a], values[b], bounds.FinestCellSize);
                        Vector3 normal = surface.GetNormal(position, bounds.FinestCellSize);
                        index = (uint)positions.Count;
                        edges.Add(edge, index);
                        positions.Add(ShiftBoundary(position, normal, bounds));
                        samples.Add(new(position, normal));
                    }
                    vertices[vertex] = index;
                }

                int indexCount = (TransvoxelTables.RegularCounts[cellClass] & 15) * 3;
                foreach (byte vertex in TransvoxelTables.RegularIndices.Slice(cellClass * 15, indexCount))
                    indices.Add(vertices[vertex]);
            }
        }
        BuildTransitions(surface, bounds, positions, samples, indices);
        return new(positions.ToArray(), indices.ToArray(), samples.ToArray());
    }

    private static Vector3 FindIntersection(TerrainSurfaceRead surface, Vector3 start, Vector3 end,
        float startValue, float endValue, float finestCellSize)
    {
        for (float width = Vector3.Distance(start, end); width > finestCellSize; width *= .5f)
        {
            Vector3 middle = (start + end) * .5f;
            float value = surface.Sample(middle);
            if ((value < 0) == (startValue < 0)) { start = middle; startValue = value; }
            else { end = middle; endValue = value; }
        }
        return Vector3.Lerp(start, end, startValue / (startValue - endValue));
    }

    private static Vector3 ShiftBoundary(Vector3 position, Vector3 normal, TerrainChunkBounds bounds)
    {
        Vector3 local = (position - bounds.Min) / bounds.CellSize;
        TerrainTransitionFaces near = TerrainTransitionFaces.None;
        Vector3 offset = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            if (local[axis] < 1f)
            {
                near |= (TerrainTransitionFaces)(1 << (axis * 2));
                offset[axis] = 1f - local[axis];
            }
            else if (local[axis] > bounds.CellCount - 1f)
            {
                near |= (TerrainTransitionFaces)(2 << (axis * 2));
                offset[axis] = bounds.CellCount - 1f - local[axis];
            }
        }
        if ((near & bounds.TransitionFaces) != near) return position;
        offset *= bounds.CellSize * .25f;
        return position + offset - Vector3.Dot(offset, normal) * normal;
    }

    private readonly record struct GridEdge(int X, int Y, int Z, int Axis);
}
