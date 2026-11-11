using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Spatial;

/// <summary>A leaf or branch in the array held by a bounds tree.</summary>
public readonly struct BoundsNode
{
    internal BoundsNode(SpatialBounds bounds, Entity entity, int parent,
        int firstChild, int secondChild, int memberCount, int height)
    {
        Bounds = bounds;
        Entity = entity;
        Parent = parent;
        FirstChild = firstChild;
        SecondChild = secondChild;
        MemberCount = memberCount;
        Height = height;
    }

    public readonly SpatialBounds Bounds;

    public Entity Entity { get; }

    public int Parent { get; }

    public int FirstChild { get; }

    public int SecondChild { get; }

    public int MemberCount { get; }

    public int Height { get; }

    public bool IsLeaf => FirstChild < 0;

    internal BoundsNode WithParent(int parent) =>
        new(Bounds, Entity, parent, FirstChild, SecondChild, MemberCount, Height);
}
