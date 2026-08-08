using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>创建空 entity（无组件）。</summary>
    public Entity CreateEntity()
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        return _entities.Create();
    }

    /// <summary>创建带一个组件的 entity，直接到位。</summary>
    public Entity CreateEntity<T1>(in T1 component)
        where T1 : struct, IComponent
    {
        Span<int> componentIds = [ComponentMetadata<T1>.Id];
        T1 state = component;
        int componentId = ComponentMetadata<T1>.Id;

        // This compiler-owned callback has no user body after its single value write. Without an
        // OnAdd/OnInsert callback for T1 or a materialized index there is no user-code failure
        // point, so the ordinary spawn path avoids cloning an ever-growing World for scalar
        // creation. Public ExecuteBundle* remains a full candidate-root transaction because its
        // arbitrary callback can fault.
        bool fastPathEligible;
        using (WorldJobAdmissionScope readAdmission = EnterJobTopologyRead())
        {
            fastPathEligible =
                !HasCreateHookCallbacks(componentId) &&
                !_indices.HasStore(componentId);
        }

        if (fastPathEligible)
        {
            using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
            // A relevant callback or index registration can win between the read probe and this
            // writer. Both facts are monotonic for the World lifetime, so one writer-owned
            // recheck is sufficient.
            if (!HasCreateHookCallbacks(componentId) && !_indices.HasStore(componentId))
            {
                PublicComponentMutationGuard.Structural<T1>(nameof(CreateEntity));
                return Bundles.ExecuteSpawn(
                    componentIds,
                    ReadOnlySpan<int>.Empty,
                    ref state,
                    static (BundleWriteView view, ref T1 value) => view.Write(in value));
            }
        }

        return ExecuteBundleSpawn(
            componentIds,
            ref state,
            static (BundleWriteView view, ref T1 value) => view.Write(in value));
    }

    /// <summary>销毁 entity。</summary>
    internal void DestroyEntityImmediate(Entity entity)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _entities.DestroyNow(entity);
    }

    public void DestroyEntity(Entity entity)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _entities.Destroy(entity);
    }

    /// <summary>检查 entity 是否存活。</summary>
    public bool IsAlive(Entity entity)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return _entities.Alive(entity);
    }

    /// <summary>当前世界中存活的 entity 总数。</summary>
    public int EntityCount
    {
        get
        {
            using WorldJobAdmissionScope admission = EnterJobTopologyRead();
            return _entities.Count;
        }
    }

    public bool IsPendingCleanup(Entity entity)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return _entities.Pending(entity);
    }

    internal int ArchetypeCount => _tables.Count;

    internal ReadOnlySpan<Archetype> AllArchetypes => _tables.All;
}

