namespace SomeEngine.ECS.Entities;

/// <summary>
/// 实体标识符。8 字节 blittable 值类型，Index + Generation 防止悬空引用。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §3.1
/// - Index=0 保留为 null，实际分配从 Index=1 开始
/// - default(Entity) == Entity.Null → 天然无效值
/// </remarks>
public readonly struct Entity : IEquatable<Entity>
{
    public static readonly Entity Null = default;

    public readonly int Index;
    public readonly int Generation;

    internal Entity(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool Equals(Entity other) =>
        Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) =>
        obj is Entity other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Index, Generation);

    public static bool operator ==(Entity left, Entity right) =>
        left.Equals(right);

    public static bool operator !=(Entity left, Entity right) =>
        !left.Equals(right);

    public override string ToString() =>
        $"Entity({Index}:{Generation})";
}

