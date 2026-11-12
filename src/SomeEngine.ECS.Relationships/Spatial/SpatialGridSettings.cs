using System.Numerics;

namespace SomeEngine.ECS.Spatial;

public interface ISpatialGridSettings
{
    int LevelCount { get; }
    float GetCellSize(int level);
    int GetLevel(in SpatialBounds bounds);
}

public readonly record struct UniformGridSettings(float CellSize) : ISpatialGridSettings
{
    public int LevelCount => 1;
    public float GetCellSize(int level) => CellSize;
    public int GetLevel(in SpatialBounds bounds) => 0;
}

public readonly record struct HierarchicalGridSettings(float CellSize, int LevelCount) : ISpatialGridSettings
{
    public float GetCellSize(int level) => MathF.ScaleB(CellSize, level);
    public int GetLevel(in SpatialBounds bounds)
    {
        Vector3 extent = bounds.Max - bounds.Min;
        float size = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        return Math.Clamp((int)MathF.Ceiling(MathF.Log2(size / CellSize)), 0, LevelCount - 1);
    }
}

public static partial class SpatialQueries
{
    public static SpatialGrid<UniformGridSettings> CreateUniformGrid(float cellSize) => new(new(cellSize));
    public static SpatialGrid<HierarchicalGridSettings> CreateHierarchicalGrid(float cellSize, int levelCount) =>
        new(new(cellSize, levelCount));
}
