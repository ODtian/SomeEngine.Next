using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public struct Parent : IComponent
{
    public Entity Value;
}

public struct Depth : IComponent
{
    public byte Value;
}

public struct ChildBuffer : ICleanupComponent
{
    public SmallList<Entity> Children;
}

internal struct HierarchyLink : ICleanupComponent
{
    public Entity Parent;
    public int ChildIndex;
}

