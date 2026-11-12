using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

internal struct EasyHierarchyParents(HierarchyObservation<EasyEcs.Entity> result) : EasyEcs.IForEachBatch<ReferenceParent<EasyEcs.Entity>>
{
    public static int Writes => 0;
    public void Execute(EasyEcs.EntitySpan entities, Span<ReferenceParent<EasyEcs.Entity>> values)
    {
        for (int i = 0; i < values.Length; i++) result.Parent(entities[i], values[i].Value);
    }
}
internal struct EasyHierarchyChildren(HierarchyObservation<EasyEcs.Entity> result) : EasyEcs.IForEachBatch<ReferenceChildren<EasyEcs.Entity>>
{
    public static int Writes => 0;
    public void Execute(EasyEcs.EntitySpan entities, Span<ReferenceChildren<EasyEcs.Entity>> values)
    {
        for (int i = 0; i < values.Length; i++) result.Children(entities[i], values[i].Items.AsSpan(0, values[i].Count));
    }
}

internal readonly struct EasyHierarchyValues(EasyEcs.World world) : IHierarchyValues<EasyEcs.Entity>
{
    public int Index(EasyEcs.Entity entity) => entity.Id.Index;
    public bool Alive(EasyEcs.Entity entity) => entity.IsAlive;
    public bool Has<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Read<T>();
    public void Set<T>(EasyEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Set(value);
    public void Remove<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Remove<T>();
    public void Destroy(EasyEcs.Entity entity) => entity.Destroy();
    public void ReadTopology(HierarchyObservation<EasyEcs.Entity> result)
    {
        var parents = new EasyHierarchyParents(result);
        var children = new EasyHierarchyChildren(result);
        world.ForEachBatch<ReferenceParent<EasyEcs.Entity>, EasyHierarchyParents>(ref parents);
        world.ForEachBatch<ReferenceChildren<EasyEcs.Entity>, EasyHierarchyChildren>(ref children);
    }
    public void Dispose() { }
    public EasyEcs.Entity Create(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.CreateEntity(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.CreateEntity(p, v, h, new Team()) : world.CreateEntity(p, v, h);
    }
}

internal readonly struct FrifloHierarchyValues(EntityStore world) : IHierarchyValues<Friflo.Engine.ECS.Entity>
{
    private readonly ArchetypeQuery<ReferenceParent<Friflo.Engine.ECS.Entity>> _parents = world.Query<ReferenceParent<Friflo.Engine.ECS.Entity>>();
    private readonly ArchetypeQuery<ReferenceChildren<Friflo.Engine.ECS.Entity>> _children = world.Query<ReferenceChildren<Friflo.Engine.ECS.Entity>>();
    public int Index(Friflo.Engine.ECS.Entity entity) => entity.Id;
    public bool Alive(Friflo.Engine.ECS.Entity entity) => !entity.IsNull;
    public bool Has<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.HasComponent<T>();
    public T Read<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.GetComponent<T>();
    public void Set<T>(Friflo.Engine.ECS.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.AddComponent(value);
    public void Remove<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.RemoveComponent<T>();
    public void Destroy(Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
    public void ReadTopology(HierarchyObservation<Friflo.Engine.ECS.Entity> result)
    {
        foreach (var (parents, entities) in _parents.Chunks)
        {
            var values = parents.Span;
            for (int i = 0; i < values.Length; i++) result.Parent(entities.EntityAt(i), values[i].Value);
        }
        foreach (var (children, entities) in _children.Chunks)
        {
            var values = children.Span;
            for (int i = 0; i < values.Length; i++) result.Children(entities.EntityAt(i), values[i].Items.AsSpan(0, values[i].Count));
        }
    }
    public void Dispose() { }
    public Friflo.Engine.ECS.Entity Create(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.CreateEntity(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.CreateEntity(p, v, h, new Team()) : world.CreateEntity(p, v, h);
    }
}

internal readonly struct ArchHierarchyValues(Arch.Core.World world) : IHierarchyValues<Arch.Core.Entity>
{
    private readonly Arch.Core.QueryDescription _parents = new Arch.Core.QueryDescription().WithAll<ReferenceParent<Arch.Core.Entity>>();
    private readonly Arch.Core.QueryDescription _children = new Arch.Core.QueryDescription().WithAll<ReferenceChildren<Arch.Core.Entity>>();
    public int Index(Arch.Core.Entity entity) => entity.Id;
    public bool Alive(Arch.Core.Entity entity) => world.IsAlive(entity);
    public bool Has<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Has<T>(entity);
    public T Read<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Get<T>(entity);
    public void Set<T>(Arch.Core.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    {
        if (world.Has<T>(entity)) world.Set(entity, value); else world.Add(entity, value);
    }
    public void Remove<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Remove<T>(entity);
    public void Destroy(Arch.Core.Entity entity) => world.Destroy(entity);
    public void ReadTopology(HierarchyObservation<Arch.Core.Entity> result)
    {
        foreach (ref var chunk in world.Query(in _parents).GetChunkIterator())
        {
            var values = chunk.GetSpan<ReferenceParent<Arch.Core.Entity>>();
            var entities = chunk.Entities;
            for (int i = 0; i < chunk.Count; i++) result.Parent(entities[i], values[i].Value);
        }
        foreach (ref var chunk in world.Query(in _children).GetChunkIterator())
        {
            var values = chunk.GetSpan<ReferenceChildren<Arch.Core.Entity>>();
            var entities = chunk.Entities;
            for (int i = 0; i < chunk.Count; i++) result.Children(entities[i], values[i].Items.AsSpan(0, values[i].Count));
        }
    }
    public void Dispose() { }
    public Arch.Core.Entity Create(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.Create(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.Create(p, v, h, new Team()) : world.Create(p, v, h);
    }
}

internal readonly struct DefaultHierarchyValues(DefaultEcs.World world) : IHierarchyValues<DefaultEcs.Entity>
{
    private readonly DefaultEcs.EntitySet _parents = world.GetEntities().With<ReferenceParent<DefaultEcs.Entity>>().AsSet();
    private readonly DefaultEcs.EntitySet _children = world.GetEntities().With<ReferenceChildren<DefaultEcs.Entity>>().AsSet();
    // DefaultEcs 0.17.2 returns its native EntityId from this public method.
    public int Index(DefaultEcs.Entity entity) => entity.GetHashCode();
    public bool Alive(DefaultEcs.Entity entity) => entity.IsAlive;
    public bool Has<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Get<T>();
    public void Set<T>(DefaultEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Set(value);
    public void Remove<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Remove<T>();
    public void Destroy(DefaultEcs.Entity entity) => entity.Dispose();
    public void ReadTopology(HierarchyObservation<DefaultEcs.Entity> result)
    {
        var parents = world.GetComponents<ReferenceParent<DefaultEcs.Entity>>();
        foreach (ref readonly var entity in _parents.GetEntities()) result.Parent(entity, parents[entity].Value);
        var children = world.GetComponents<ReferenceChildren<DefaultEcs.Entity>>();
        foreach (ref readonly var entity in _children.GetEntities())
        {
            ref readonly var value = ref children[entity];
            result.Children(entity, value.Items.AsSpan(0, value.Count));
        }
    }
    public void Dispose() { _parents.Dispose(); _children.Dispose(); }
    public DefaultEcs.Entity Create(int ordinal)
    {
        var entity = world.CreateEntity();
        entity.Set(new Position { X = ordinal }); entity.Set(new Velocity { X = 1, Y = 2 });
        entity.Set(new Health { Value = 100, Max = 100 });
        if (ordinal % 2 == 0) entity.Set(new Team());
        if (ordinal % 4 == 0) entity.Set(new Sleeping());
        return entity;
    }
}

internal sealed partial class EasyEcsWorld
{
    private void PrepareHierarchy(string workload)
        => HierarchyScenario ??= new HierarchyScenario<EasyEcs.Entity, ReferenceHierarchy<EasyEcs.Entity, EasyHierarchyValues>>(
            new(new(_world), _entities), _entities, workload.Contains("deep"), workload.EndsWith("ordered"));
}
internal sealed partial class FrifloWorld
{
    private void PrepareHierarchy(string workload)
        => HierarchyScenario ??= new HierarchyScenario<Friflo.Engine.ECS.Entity, ReferenceHierarchy<Friflo.Engine.ECS.Entity, FrifloHierarchyValues>>(
            new(new(_world), _entities), _entities, workload.Contains("deep"), workload.EndsWith("ordered"));
}
internal sealed partial class ArchWorld
{
    private void PrepareHierarchy(string workload)
        => HierarchyScenario ??= new HierarchyScenario<Arch.Core.Entity, ReferenceHierarchy<Arch.Core.Entity, ArchHierarchyValues>>(
            new(new(_world), _entities), _entities, workload.Contains("deep"), workload.EndsWith("ordered"));
}
internal sealed partial class DefaultEcsWorld
{
    private void PrepareHierarchy(string workload)
        => HierarchyScenario ??= new HierarchyScenario<DefaultEcs.Entity, ReferenceHierarchy<DefaultEcs.Entity, DefaultHierarchyValues>>(
            new(new(_world), _entities), _entities, workload.Contains("deep"), workload.EndsWith("ordered"));
}
