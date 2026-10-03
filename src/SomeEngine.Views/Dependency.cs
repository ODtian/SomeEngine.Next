using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A component read by the view and its write version at the time of the read.</summary>
public struct Dependency : IBufferElement
{
    public Entity Entity;
    public ushort Component;
    public uint Version;
}
