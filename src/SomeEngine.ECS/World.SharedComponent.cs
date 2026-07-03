using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    public void AddShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _shared.Add(entity, in value);
    }

    public void ReplaceShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _shared.Replace(entity, in value);
    }

    internal void MergeShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _shared.Merge(entity, in value);
    }

    public T GetShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        return _shared.Get<T>(entity);
    }

    public void RemoveShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        _shared.Remove<T>(entity);
    }

    public bool HasShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        return _shared.Has<T>(entity);
    }

}

