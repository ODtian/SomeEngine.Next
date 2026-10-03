using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>A sampled value to be blended into one field of the entity holding this buffer.</summary>
[BufferCapacity(4)]
public struct Contribution<T> : IBufferElement where T : unmanaged
{
    public Channel<T> Channel;
    public byte Layer;
    public T Value;
    public Entity Source;
    public uint Output;
}

/// <summary>A field's reference value, written by the caller through PropertyAnimation.SetBase.</summary>
[BufferCapacity(4)]
public struct BaseValue<T> : IBufferElement where T : unmanaged
{
    public Channel<T> Channel;
    public T Value;
}
