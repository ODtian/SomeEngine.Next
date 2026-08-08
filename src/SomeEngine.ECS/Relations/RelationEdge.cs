using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

/// <summary>
/// Typed identity of one relation edge. Endpoint pairs are deliberately not
/// convertible to this handle because parallel edges may share a pair.
/// </summary>
public readonly struct RelationEdge<T> : IEquatable<RelationEdge<T>>
    where T : struct, IComponent
{
    public RelationEdge(Entity entity)
    {
        Entity = entity;
    }

    public Entity Entity { get; }

    public bool IsNull => Entity == Entity.Null;

    public bool Equals(RelationEdge<T> other) => Entity == other.Entity;

    public override bool Equals(object? obj) =>
        obj is RelationEdge<T> other && Equals(other);

    public override int GetHashCode() => Entity.GetHashCode();

    public override string ToString() => $"{typeof(T).Name} edge {Entity}";

    public static bool operator ==(RelationEdge<T> left, RelationEdge<T> right) =>
        left.Equals(right);

    public static bool operator !=(RelationEdge<T> left, RelationEdge<T> right) =>
        !left.Equals(right);
}
