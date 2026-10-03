using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A component value or presence observed by the view.</summary>
public struct Dependency : IBufferElement
{
    public Entity Entity;
    public ushort Component;
    public uint Version;
    public DependencyKind Kind;
}

/// <summary>The component fact that causes a view to rebuild when it changes.</summary>
public enum DependencyKind : byte
{
    ComponentValue,
    ComponentPresence,
}
