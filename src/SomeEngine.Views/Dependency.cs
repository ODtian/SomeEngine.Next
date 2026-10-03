using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A component read by the view, its source, and its observed write version.</summary>
public struct Dependency : IBufferElement
{
    public DependencyKind Kind;
    public Entity Entity;
    public ushort Component;
    public uint Version;
}

/// <summary>The data observed by a view dependency.</summary>
public enum DependencyKind : byte
{
    Component,
    Environment,
}
