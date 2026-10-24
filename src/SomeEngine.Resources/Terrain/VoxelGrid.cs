using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>A sparse three-dimensional lattice in terrain-local metres.</summary>
public readonly record struct VoxelGrid(int PageSize, float SampleSpacing = 1f)
{
    public Vector3 SamplePosition(int x, int y, int z) =>
        new(x * SampleSpacing, y * SampleSpacing, z * SampleSpacing);

    internal int PageCoordinate(int coordinate) =>
        coordinate >= 0 ? coordinate / PageSize : (coordinate + 1) / PageSize - 1;
}

/// <summary>A box in global sample coordinates; maximum coordinates are exclusive.</summary>
public readonly record struct VoxelRegion(int MinX, int MinY, int MinZ, int Width, int Height, int Depth)
{
    public int MaxX => MinX + Width;
    public int MaxY => MinY + Height;
    public int MaxZ => MinZ + Depth;

    public VoxelRegion Expand(int border) => new(MinX - border, MinY - border, MinZ - border,
        Width + 2 * border, Height + 2 * border, Depth + 2 * border);

    public VoxelRegion Intersect(VoxelRegion other)
    {
        int x = Math.Max(MinX, other.MinX);
        int y = Math.Max(MinY, other.MinY);
        int z = Math.Max(MinZ, other.MinZ);
        return new VoxelRegion(x, y, z,
            Math.Max(0, Math.Min(MaxX, other.MaxX) - x),
            Math.Max(0, Math.Min(MaxY, other.MaxY) - y),
            Math.Max(0, Math.Min(MaxZ, other.MaxZ) - z));
    }
}
