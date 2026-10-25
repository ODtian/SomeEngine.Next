namespace SomeEngine.Physics;

public enum CollisionAxis { X, Y, Z }

public interface ICollisionIntervalSettings
{
    CollisionAxis Axis { get; }
    int GetPartition(float coordinate);
}

public readonly record struct SapSettings(CollisionAxis Axis) : ICollisionIntervalSettings
{
    public int GetPartition(float coordinate) => 0;
}

public readonly record struct PartitionedSapSettings(CollisionAxis Axis, float PartitionSize)
    : ICollisionIntervalSettings
{
    public int GetPartition(float coordinate) => (int)MathF.Floor(coordinate / PartitionSize);
}

public static partial class Collision
{
    public static CollisionIntervalIndex<SapSettings> CreateSap(CollisionAxis axis) => new(new(axis));
    public static CollisionIntervalIndex<PartitionedSapSettings> CreatePartitionedSap(CollisionAxis axis,
        float partitionSize) => new(new(axis, partitionSize));
}
