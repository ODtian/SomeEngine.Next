using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>Blends voxel values toward a cubic neighborhood mean with linear radial falloff.</summary>
public readonly record struct VoxelSmoothBrush : IVoxelBrushCpu
{
    public VoxelSmoothBrush(VoxelGrid grid, Vector3 center, float radius, int neighborRadius, float strength = 1f)
    {
        Center = center;
        Radius = radius;
        NeighborRadius = neighborRadius;
        Strength = strength;
        Range = TerrainBrushArea.GetRange(grid, center, radius);
    }

    public Vector3 Center { get; }
    public float Radius { get; }
    public int NeighborRadius { get; }
    public float Strength { get; }
    public VoxelRegion Range { get; }
    public int ReadBorder => NeighborRadius;

    public float Apply(VoxelFieldRead source, int x, int y, int z)
    {
        float total = 0f;
        int count = 0;
        for (int sampleZ = z - NeighborRadius; sampleZ <= z + NeighborRadius; sampleZ++)
        for (int sampleY = y - NeighborRadius; sampleY <= y + NeighborRadius; sampleY++)
        for (int sampleX = x - NeighborRadius; sampleX <= x + NeighborRadius; sampleX++)
        {
            total += source[sampleX, sampleY, sampleZ];
            count++;
        }
        float weight = TerrainBrushArea.GetWeight(Vector3.Distance(source.Grid.SamplePosition(x, y, z), Center), Radius, Strength);
        return float.Lerp(source[x, y, z], total / count, weight);
    }
}

/// <summary>Blends voxel values toward a terrain-local signed plane with linear radial falloff.</summary>
public readonly record struct VoxelFlattenBrush : IVoxelBrushCpu
{
    public VoxelFlattenBrush(VoxelGrid grid, Vector3 center, float radius, Plane plane, float strength = 1f)
    {
        Center = center;
        Radius = radius;
        TargetPlane = Plane.Normalize(plane);
        Strength = strength;
        Range = TerrainBrushArea.GetRange(grid, center, radius);
    }

    public Vector3 Center { get; }
    public float Radius { get; }
    public Plane TargetPlane { get; }
    public float Strength { get; }
    public VoxelRegion Range { get; }
    public int ReadBorder => 0;

    public float Apply(VoxelFieldRead source, int x, int y, int z)
    {
        Vector3 position = source.Grid.SamplePosition(x, y, z);
        float value = Plane.DotCoordinate(TargetPlane, position);
        float weight = TerrainBrushArea.GetWeight(Vector3.Distance(position, Center), Radius, Strength);
        return float.Lerp(source[x, y, z], value, weight);
    }
}
