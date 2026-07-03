using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
namespace SomeEngine.ECS;

public partial class World
{
    public DynamicBuffer<T> GetBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        return Buffers.Get<T>(entity);
    }

    public void AddBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        Buffers.Add<T>(entity);
    }

    public bool HasBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        return Buffers.Has<T>(entity);
    }

    public void RemoveBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        Buffers.Remove<T>(entity);
    }

}

