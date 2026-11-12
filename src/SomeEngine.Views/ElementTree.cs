using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>The ordered hierarchy of view entities.</summary>
public readonly struct ElementTree : IHierarchyDomain
{
    internal static Entity[] CopyChildren(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
}

/// <summary>The number identifying the view's statically registered functions.</summary>
public struct ViewType : IComponent
{
    public ushort Id;
    internal bool RebuildRequired;
}

/// <summary>The child's key and its zero-based occurrence among siblings with that key.</summary>
public struct ElementKey : IComponent
{
    public ulong Key;
    public int Occurrence;
}

/// <summary>Marks view-local state for development checks of repeated sibling keys.</summary>
public readonly struct LocalState : ITag;
