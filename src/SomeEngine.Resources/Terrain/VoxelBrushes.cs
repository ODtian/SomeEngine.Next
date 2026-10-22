using System.Numerics;

namespace SomeEngine.Resources;

/// <summary>A value-type CPU brush, evaluated against one fixed input version.</summary>
public interface IVoxelBrushCpu
{
    /// <summary>Samples written by this brush, in global X/Y/Z sample coordinates.</summary>
    VoxelRegion Range { get; }
    /// <summary>Additional input samples around each written page region.</summary>
    int ReadBorder { get; }
    /// <summary>Computes one new scalar using samples inside the supplied source region.</summary>
    float Apply(VoxelFieldRead source, int x, int y, int z);
}

/// <summary>Adds a scalar delta with linear radial falloff. Centre and radius use terrain-local metres.</summary>
public readonly record struct VoxelSphereDeltaBrush : IVoxelBrushCpu
{
    public VoxelSphereDeltaBrush(VoxelGrid grid, Vector3 center, float radius, float delta)
    {
        Center = center;
        Radius = radius;
        Delta = delta;
        Vector3 minimum = (center - new Vector3(radius)) / grid.SampleSpacing;
        Vector3 maximum = (center + new Vector3(radius)) / grid.SampleSpacing;
        int x = (int)MathF.Floor(minimum.X);
        int y = (int)MathF.Floor(minimum.Y);
        int z = (int)MathF.Floor(minimum.Z);
        Range = new VoxelRegion(x, y, z,
            (int)MathF.Ceiling(maximum.X) - x + 1,
            (int)MathF.Ceiling(maximum.Y) - y + 1,
            (int)MathF.Ceiling(maximum.Z) - z + 1);
    }

    public Vector3 Center { get; }
    public float Radius { get; }
    public float Delta { get; }
    public VoxelRegion Range { get; }
    public int ReadBorder => 0;

    public float Apply(VoxelFieldRead source, int x, int y, int z)
    {
        float distance = Vector3.Distance(source.Grid.SamplePosition(x, y, z), Center);
        float weight = MathF.Max(0f, 1f - distance / Radius);
        return source[x, y, z] + Delta * weight;
    }
}
