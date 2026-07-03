using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>创建空 entity（无组件）。</summary>
    public Entity CreateEntity()
    {
        return _entities.Create();
    }

    /// <summary>创建带一个组件的 entity，直接到位。</summary>
    public Entity CreateEntity<T1>(in T1 component)
        where T1 : struct, IComponent
    {
        Span<int> componentIds = [ComponentMetadata<T1>.Id];
        var writer = CreateSpawnWriter(componentIds);
        writer.Write(component);
        return writer.Entity;
    }

    /// <summary>销毁 entity。</summary>
    internal void DestroyEntityImmediate(Entity entity)
    {
        _entities.DestroyNow(entity);
    }

    public void DestroyEntity(Entity entity)
    {
        _entities.Destroy(entity);
    }

    /// <summary>检查 entity 是否存活。</summary>
    public bool IsAlive(Entity entity) => _entities.Alive(entity);

    /// <summary>当前世界中存活的 entity 总数。</summary>
    public int EntityCount => _entities.Count;

    public bool IsPendingCleanup(Entity entity)
    {
        return _entities.Pending(entity);
    }

    internal int ArchetypeCount => _tables.Count;

    internal IReadOnlyList<Archetype> AllArchetypes => _tables.All;
}

