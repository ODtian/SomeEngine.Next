using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Transparency.Research;

public sealed class TileVisibilityGeometry
{
    public const int TileSize = 16;
    public TileVisibilityGeometry(TriangleHierarchy geometry, int width, int height)
    {
        TileColumns = (width + TileSize - 1) / TileSize;
        TileRows = (height + TileSize - 1) / TileSize;
        var tiles = Enumerable.Range(0, TileColumns * TileRows).Select(static _ => new List<uint>()).ToArray();
        Triangles = new TileTriangleData[geometry.Triangles.Length];
        uint association = 0;
        for (int index = 0; index < Triangles.Length; index++)
        {
            ProjectedTriangle triangle = geometry.Triangles[index];
            TriangleCoverage coverage = geometry.Coverage[index];
            Vector3 minimum = triangle.Minimum, maximum = triangle.Maximum;
            int left = Math.Clamp((int)MathF.Floor((minimum.X * .5f + .5f) * width / TileSize), 0, TileColumns - 1);
            int right = Math.Clamp((int)MathF.Floor((maximum.X * .5f + .5f) * width / TileSize), 0, TileColumns - 1);
            int top = Math.Clamp((int)MathF.Floor((.5f - maximum.Y * .5f) * height / TileSize), 0, TileRows - 1);
            int bottom = Math.Clamp((int)MathF.Floor((.5f - minimum.Y * .5f) * height / TileSize), 0, TileRows - 1);
            float depthB = coverage.EdgeB.W - coverage.EdgeA.W, depthC = coverage.EdgeC.W - coverage.EdgeA.W;
            Vector3 plane = new(coverage.EdgeB.X * depthB + coverage.EdgeC.X * depthC,
                coverage.EdgeB.Y * depthB + coverage.EdgeC.Y * depthC,
                coverage.EdgeA.W + coverage.EdgeB.Z * depthB + coverage.EdgeC.Z * depthC);
            Triangles[index] = new(plane, triangle.TriangleId, coverage.ColorIndex, association,
                (uint)left | ((uint)top << 16), (uint)(right - left + 1));
            for (int row = top; row <= bottom; row++)
            for (int column = left; column <= right; column++)
            {
                tiles[row * TileColumns + column].Add((uint)index);
                association++;
            }
        }
        Associations = new uint[association];
        Ranges = new TileRange[tiles.Length];
        int first = 0;
        for (int index = 0; index < tiles.Length; index++)
        {
            Ranges[index] = new((uint)first, (uint)tiles[index].Count);
            tiles[index].CopyTo(Associations, first);
            first += tiles[index].Count;
        }
    }

    public int TileColumns { get; }
    public int TileRows { get; }
    public TileTriangleData[] Triangles { get; }
    public uint[] Associations { get; }
    public TileRange[] Ranges { get; }
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TileTriangleData(Vector3 DepthPlane, uint TriangleId,
    uint ColorIndex, uint FirstAssociation, uint MinimumTile, uint TileWidth);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TileRange(uint FirstTriangle, uint TriangleCount);
