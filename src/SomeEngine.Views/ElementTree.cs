using SomeEngine.ECS;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>The ordered hierarchy of view entities.</summary>
public readonly struct ElementTree : IHierarchyDomain;

/// <summary>The number identifying the view's statically registered functions.</summary>
public struct ViewType : IComponent
{
    public ushort Id;
}
