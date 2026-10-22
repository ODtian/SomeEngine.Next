using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>A height lattice in terrain-local metres, with X/Z sample coordinates.</summary>
public readonly record struct HeightGrid(int Width, int Depth, int PageSize, float SampleSpacing = 1f)
{
    public HeightRegion Range => new(0, 0, Width, Depth);
    public Vector2 SamplePosition(int x, int z) => new(x * SampleSpacing, z * SampleSpacing);
}

/// <summary>A rectangular sample region; maximum coordinates are exclusive.</summary>
public readonly record struct HeightRegion(int MinX, int MinZ, int Width, int Depth)
{
    public int MaxX => MinX + Width;
    public int MaxZ => MinZ + Depth;

    public HeightRegion Expand(int border) =>
        new(MinX - border, MinZ - border, Width + 2 * border, Depth + 2 * border);

    public HeightRegion Intersect(HeightRegion other)
    {
        int x = Math.Max(MinX, other.MinX);
        int z = Math.Max(MinZ, other.MinZ);
        return new HeightRegion(x, z,
            Math.Max(0, Math.Min(MaxX, other.MaxX) - x),
            Math.Max(0, Math.Min(MaxZ, other.MaxZ) - z));
    }
}
