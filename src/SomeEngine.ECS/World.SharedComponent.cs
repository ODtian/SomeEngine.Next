using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    public void AddShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _shared.Add(entity, in value);
    }

    public void ReplaceShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _shared.Replace(entity, in value);
    }

    internal void MergeShared<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _shared.Merge(entity, in value);
    }

    public T GetShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        RequireJobSharedRead<T>("Shared-component");
        return _shared.Get<T>(entity);
    }

    public void RemoveShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _shared.Remove<T>(entity);
    }

    public bool HasShared<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        // Presence is encoded by archetype/chunk topology. This path never loads T from the
        // shared-value store, so an alias-shape check would reject a value that cannot escape.
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return _shared.Has<T>(entity);
    }

}

