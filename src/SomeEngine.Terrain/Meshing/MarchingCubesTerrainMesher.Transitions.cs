using System.Numerics;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

public sealed partial class MarchingCubesTerrainMesher
{
    private static void BuildTransitions(TerrainSurfaceRead surface, TerrainChunkBounds bounds,
        List<Vector3> positions, List<TerrainSurfacePoint> samples, List<uint> indices)
    {
        ReadOnlySpan<int> weights = [1, 2, 4, 128, 256, 8, 64, 32, 16];
        ReadOnlySpan<int> coarseCorners = [0, 2, 6, 8];
        Span<Vector3> corners = stackalloc Vector3[13];
        Span<float> values = stackalloc float[13];
        Span<uint> vertices = stackalloc uint[12];
        for (int face = 0; face < 6; face++)
        {
            if (((int)bounds.TransitionFaces & (1 << face)) == 0) continue;
            (Vector3 u, Vector3 v) = face switch
            {
                0 => (Vector3.UnitY, Vector3.UnitZ), 1 => (Vector3.UnitZ, Vector3.UnitY),
                2 => (Vector3.UnitZ, Vector3.UnitX), 3 => (Vector3.UnitX, Vector3.UnitZ),
                4 => (Vector3.UnitX, Vector3.UnitY), _ => (Vector3.UnitY, Vector3.UnitX),
            };
            Vector3 origin = bounds.Min;
            if ((face & 1) != 0) origin[face / 2] = bounds.Max[face / 2];
            for (int y = 0; y < bounds.CellCount; y++)
            for (int x = 0; x < bounds.CellCount; x++)
            {
                int cellCase = 0;
                for (int corner = 0; corner < 9; corner++)
                {
                    Vector3 point = origin + (u * (x + (corner % 3) * .5f)
                        + v * (y + (corner / 3) * .5f)) * bounds.CellSize;
                    corners[corner] = point;
                    values[corner] = surface.Sample(point);
                    if (values[corner] < 0) cellCase |= weights[corner];
                }
                for (int corner = 0; corner < 4; corner++)
                {
                    corners[9 + corner] = corners[coarseCorners[corner]];
                    values[9 + corner] = values[coarseCorners[corner]];
                }

                int encodedClass = TransvoxelTables.TransitionClasses[cellCase];
                int cellClass = encodedClass & 127;
                int vertexCount = TransvoxelTables.TransitionCounts[cellClass] >> 4;
                ReadOnlySpan<ushort> edges = TransvoxelTables.TransitionEdges.Slice(cellCase * 12, vertexCount);
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int a = (edges[vertex] >> 4) & 15, b = edges[vertex] & 15;
                    Vector3 point = FindIntersection(surface, corners[a], corners[b], values[a], values[b], bounds.FinestCellSize);
                    Vector3 normal = surface.GetNormal(point, bounds.FinestCellSize);
                    vertices[vertex] = (uint)positions.Count;
                    positions.Add(a >= 9 ? ShiftBoundary(point, normal, bounds) : point);
                    samples.Add(new(point, normal));
                }
                int indexCount = (TransvoxelTables.TransitionCounts[cellClass] & 15) * 3;
                ReadOnlySpan<byte> triangles = TransvoxelTables.TransitionIndices.Slice(cellClass * 36, indexCount);
                for (int index = 0; index < indexCount; index += 3)
                {
                    uint a = vertices[triangles[index]];
                    uint b = vertices[triangles[index + ((encodedClass & 128) == 0 ? 1 : 2)]];
                    uint c = vertices[triangles[index + ((encodedClass & 128) == 0 ? 2 : 1)]];
                    // Primary positions collapse transition triangles where adjacent coarse faces meet.
                    if (positions[(int)a] == positions[(int)b] || positions[(int)b] == positions[(int)c]
                        || positions[(int)c] == positions[(int)a]) continue;
                    indices.Add(a); indices.Add(b); indices.Add(c);
                }
            }
        }
    }
}
