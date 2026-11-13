using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>A leaf or branch in the array held by a collision tree.</summary>
public readonly struct CollisionNode
{
    internal CollisionNode(CollisionBounds bounds, Entity entity, int parent,
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

    public readonly CollisionBounds Bounds;

    public Entity Entity { get; }

    public int Parent { get; }

    public int FirstChild { get; }

    public int SecondChild { get; }

    public int MemberCount { get; }

    public int Height { get; }

    public bool IsLeaf => FirstChild < 0;

    internal CollisionNode WithParent(int parent) =>
        new(Bounds, Entity, parent, FirstChild, SecondChild, MemberCount, Height);
}
