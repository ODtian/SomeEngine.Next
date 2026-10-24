using System.Numerics;

namespace SomeEngine.Resources;

internal static class TerrainBrushArea
{
    internal static HeightRegion GetRange(HeightGrid grid, Vector2 center, float radius)
    {
        Vector2 minimum = (center - new Vector2(radius)) / grid.SampleSpacing;
        Vector2 maximum = (center + new Vector2(radius)) / grid.SampleSpacing;
        int x = (int)MathF.Floor(minimum.X), z = (int)MathF.Floor(minimum.Y);
        return new(x, z, (int)MathF.Ceiling(maximum.X) - x + 1, (int)MathF.Ceiling(maximum.Y) - z + 1);
    }

    internal static VoxelRegion GetRange(VoxelGrid grid, Vector3 center, float radius)
    {
        Vector3 minimum = (center - new Vector3(radius)) / grid.SampleSpacing;
        Vector3 maximum = (center + new Vector3(radius)) / grid.SampleSpacing;
        int x = (int)MathF.Floor(minimum.X), y = (int)MathF.Floor(minimum.Y), z = (int)MathF.Floor(minimum.Z);
        return new(x, y, z, (int)MathF.Ceiling(maximum.X) - x + 1,
            (int)MathF.Ceiling(maximum.Y) - y + 1, (int)MathF.Ceiling(maximum.Z) - z + 1);
    }

    internal static float GetWeight(float distance, float radius, float strength) =>
        strength * MathF.Max(0f, 1f - distance / radius);
}
