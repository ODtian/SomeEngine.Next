using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Transparency.Research;

public sealed class TileVisibilityGeometry
{
    public const int TileSize = 16;
    public TileVisibilityGeometry(TriangleHierarchy geometry, int width, int height)
    {
        TileColumns = (width + TileSize - 1) / TileSize;
        TileRows = (height + TileSize - 1) / TileSize;
        Triangles = new TileTriangleData[geometry.Triangles.Length];
        var coverage = new TileCoverage[Triangles.Length];
        Scheduler jobs = JobSystem.Current;
        jobs.ExecuteParallel(new PrepareTriangles(geometry, width, height, TileColumns, TileRows, Triangles, coverage),
            Triangles.Length, JobScheduleOptions.AutomaticBatchSize,
            [JobResourceAccess.Read(jobs, geometry.Triangles), JobResourceAccess.Read(jobs, geometry.Coverage),
             JobResourceAccess.Write(jobs, Triangles), JobResourceAccess.Write(jobs, coverage)]);
        uint association = 0;
        for (int index = 0; index < Triangles.Length; index++)
        {
            Triangles[index] = Triangles[index] with { FirstAssociation = association };
            association += (uint)coverage[index].Count;
        }
        var references = new TileReferenceData(coverage, TileColumns, TileRows);
        Associations = references.References;
        Ranges = references.Ranges;
    }

    private readonly struct PrepareTriangles(TriangleHierarchy geometry, int width, int height,
        int columns, int rows, TileTriangleData[] triangles, TileCoverage[] output) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ProjectedTriangle triangle = geometry.Triangles[index];
            TriangleCoverage coverage = geometry.Coverage[index];
            Vector3 minimum = triangle.Minimum, maximum = triangle.Maximum;
            int left = Math.Clamp((int)MathF.Floor((minimum.X * .5f + .5f) * width / TileSize), 0, columns - 1);
            int right = Math.Clamp((int)MathF.Floor((maximum.X * .5f + .5f) * width / TileSize), 0, columns - 1);
            int top = Math.Clamp((int)MathF.Floor((.5f - maximum.Y * .5f) * height / TileSize), 0, rows - 1);
            int bottom = Math.Clamp((int)MathF.Floor((.5f - minimum.Y * .5f) * height / TileSize), 0, rows - 1);
            float depthB = coverage.EdgeB.W - coverage.EdgeA.W, depthC = coverage.EdgeC.W - coverage.EdgeA.W;
            Vector3 plane = new(coverage.EdgeB.X * depthB + coverage.EdgeC.X * depthC,
                coverage.EdgeB.Y * depthB + coverage.EdgeC.Y * depthC,
                coverage.EdgeA.W + coverage.EdgeB.Z * depthB + coverage.EdgeC.Z * depthC);
            triangles[index] = new(plane, triangle.TriangleId, coverage.ColorIndex, 0,
                (uint)left | ((uint)top << 16), (uint)(right - left + 1));
            output[index] = new(left, top, right, bottom);
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
