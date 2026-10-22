using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>Blends heights toward a square neighborhood mean with linear radial falloff.</summary>
public readonly record struct HeightSmoothBrush : IHeightBrushCpu
{
    public HeightSmoothBrush(HeightGrid grid, Vector2 center, float radius, int neighborRadius, float strength = 1f)
    {
        Center = center;
        Radius = radius;
        NeighborRadius = neighborRadius;
        Strength = strength;
        Range = TerrainBrushArea.GetRange(grid, center, radius);
    }

    public Vector2 Center { get; }
    public float Radius { get; }
    public int NeighborRadius { get; }
    public float Strength { get; }
    public HeightRegion Range { get; }
    public int ReadBorder => NeighborRadius;

    public float Apply(HeightFieldRead source, int x, int z)
    {
        float total = 0f;
        int count = 0;
        int minX = Math.Max(0, x - NeighborRadius), maxX = Math.Min(source.Grid.Width - 1, x + NeighborRadius);
        int minZ = Math.Max(0, z - NeighborRadius), maxZ = Math.Min(source.Grid.Depth - 1, z + NeighborRadius);
        for (int sampleZ = minZ; sampleZ <= maxZ; sampleZ++)
        for (int sampleX = minX; sampleX <= maxX; sampleX++)
        {
            total += source[sampleX, sampleZ];
            count++;
        }
        float weight = TerrainBrushArea.GetWeight(Vector2.Distance(source.Grid.SamplePosition(x, z), Center), Radius, Strength);
        return float.Lerp(source[x, z], total / count, weight);
    }
}

/// <summary>Blends heights toward a terrain-local plane with linear radial falloff.</summary>
public readonly record struct HeightFlattenBrush : IHeightBrushCpu
{
    public HeightFlattenBrush(HeightGrid grid, Vector2 center, float radius, Plane plane, float strength = 1f)
    {
        Center = center;
        Radius = radius;
        TargetPlane = Plane.Normalize(plane);
        Strength = strength;
        Range = TerrainBrushArea.GetRange(grid, center, radius);
    }

    public Vector2 Center { get; }
    public float Radius { get; }
    public Plane TargetPlane { get; }
    public float Strength { get; }
    public HeightRegion Range { get; }
    public int ReadBorder => 0;

    public float Apply(HeightFieldRead source, int x, int z)
    {
        Vector2 position = source.Grid.SamplePosition(x, z);
        float height = -(TargetPlane.Normal.X * position.X + TargetPlane.Normal.Z * position.Y + TargetPlane.D)
            / TargetPlane.Normal.Y;
        float weight = TerrainBrushArea.GetWeight(Vector2.Distance(position, Center), Radius, Strength);
        return float.Lerp(source[x, z], height, weight);
    }
}
