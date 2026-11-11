using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal interface INativeArchiveValues : IHierarchyValues<Entity>, IRelationValues<Entity, Entity, SaveEdge>, IReferenceSparseAccess<Entity>
{
    NativeArchiveStore Store { get; }
    Entity Create(ulong componentTypes = 0);
    new int Index(Entity entity);
    new Entity Identity(Entity entity);
    new bool Alive(Entity entity);
    new bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    new T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    new void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    new void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    new void Destroy(Entity entity);
}

internal static class NativeFullArchiveWorld
{
    internal const ulong EdgeColumns = (1UL << 18) | (1UL << 19);
    internal static SaveLink[] Links(Entity self, Entity next, int ordinal)
    {
        var links = new SaveLink[4];
        WriteLinks(links, self, next, ordinal);
        return links;
    }

    internal static void WriteLinks(Span<SaveLink> links, Entity self, Entity next, int ordinal)
    {
        links[0] = new(self, ordinal * 4L);
        links[1] = new(next, ordinal * 4L + 1);
        links[2] = new(self, ordinal * 4L + 2);
        links[3] = new(next, ordinal * 4L + 3);
    }
}
internal readonly struct EasyArchiveValues(EasyArchiveStore store) : INativeArchiveValues
{
    public NativeArchiveStore Store => store;
    public Entity Create(ulong componentTypes = 0) => store.Create(componentTypes);

    public int Index(Entity entity) => entity.Id;
    public Entity Identity(Entity entity) => entity;
    public bool Alive(Entity entity) => store.IsAlive(entity);
    public bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.HasValue<T>(entity);
    public T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.ReadValue<T>(entity);
    public void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.SetValue(entity, value);
    public void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.RemoveValue<T>(entity);
    public Entity Create(int ordinal) => store.Create();
    public Entity CreateEndpoint(int ordinal) => store.Create();
    public Entity CreateEdge(in ReferenceRelationEndpoints<Entity> endpoints, in SaveEdge payload)
    { Entity entity = store.Create(NativeFullArchiveWorld.EdgeColumns); store.SetValue(entity, endpoints); store.SetValue(entity, payload); return entity; }
    public void Destroy(Entity entity) => store.Destroy(entity);
    public void Dispose() { }
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceParent<Entity>>(entity)) result.Parent(entity, store.ReadValue<ReferenceParent<Entity>>(entity).Value);
            if (store.HasValue<ReferenceChildren<Entity>>(entity))
            { var children = store.ReadValue<ReferenceChildren<Entity>>(entity); result.Children(entity, children.Items.AsSpan(0, children.Count)); }
        }
    }
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceRelationEndpoints<Entity>>(entity))
            {
                var endpoints = store.ReadValue<ReferenceRelationEndpoints<Entity>>(entity); SaveEdge payload = store.ReadValue<SaveEdge>(entity);
                result.Main(entity, endpoints.Source, endpoints.Target, payload.Ordinal, payload.Value);
                if (endpoints.Added > since) result.Fact(entity, 1);
                if (endpoints.Written > since) result.Fact(entity, 2);
            }
            if (store.HasValue<ReferenceOldRelationEndpoints<Entity>>(entity))
            { var old = store.ReadValue<ReferenceOldRelationEndpoints<Entity>>(entity); if (old.Version > since) result.Removed(entity, old.Source, old.Target); }
        }
    }
    public int Id(Entity entity) => entity.Id;
    public int Resolve(Entity entity, bool bind)
    { if (!store.IsAlive(entity)) throw new ArgumentException("Entity generation has expired."); return entity.Id; }
    public bool Matches(Entity entity) => store.HasValue<Position>(entity) && store.HasValue<Team>(entity);
}

internal readonly struct FrifloArchiveValues(FrifloArchiveStore store) : INativeArchiveValues
{
    public NativeArchiveStore Store => store;
    public Entity Create(ulong componentTypes = 0) => store.Create(componentTypes);

    public int Index(Entity entity) => entity.Id;
    public Entity Identity(Entity entity) => entity;
    public bool Alive(Entity entity) => store.IsAlive(entity);
    public bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.HasValue<T>(entity);
    public T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.ReadValue<T>(entity);
    public void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.SetValue(entity, value);
    public void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.RemoveValue<T>(entity);
    public Entity Create(int ordinal) => store.Create();
    public Entity CreateEndpoint(int ordinal) => store.Create();
    public Entity CreateEdge(in ReferenceRelationEndpoints<Entity> endpoints, in SaveEdge payload)
    { Entity entity = store.Create(NativeFullArchiveWorld.EdgeColumns); store.SetValue(entity, endpoints); store.SetValue(entity, payload); return entity; }
    public void Destroy(Entity entity) => store.Destroy(entity);
    public void Dispose() { }
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceParent<Entity>>(entity)) result.Parent(entity, store.ReadValue<ReferenceParent<Entity>>(entity).Value);
            if (store.HasValue<ReferenceChildren<Entity>>(entity))
            { var children = store.ReadValue<ReferenceChildren<Entity>>(entity); result.Children(entity, children.Items.AsSpan(0, children.Count)); }
        }
    }
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceRelationEndpoints<Entity>>(entity))
            {
                var endpoints = store.ReadValue<ReferenceRelationEndpoints<Entity>>(entity); SaveEdge payload = store.ReadValue<SaveEdge>(entity);
                result.Main(entity, endpoints.Source, endpoints.Target, payload.Ordinal, payload.Value);
                if (endpoints.Added > since) result.Fact(entity, 1);
                if (endpoints.Written > since) result.Fact(entity, 2);
            }
            if (store.HasValue<ReferenceOldRelationEndpoints<Entity>>(entity))
            { var old = store.ReadValue<ReferenceOldRelationEndpoints<Entity>>(entity); if (old.Version > since) result.Removed(entity, old.Source, old.Target); }
        }
    }
    public int Id(Entity entity) => entity.Id;
    public int Resolve(Entity entity, bool bind)
    { if (!store.IsAlive(entity)) throw new ArgumentException("Entity generation has expired."); return entity.Id; }
    public bool Matches(Entity entity) => store.HasValue<Position>(entity) && store.HasValue<Team>(entity);
}

internal readonly struct ArchArchiveValues(ArchArchiveStore store) : INativeArchiveValues
{
    public NativeArchiveStore Store => store;
    public Entity Create(ulong componentTypes = 0) => store.Create(componentTypes);

    public int Index(Entity entity) => entity.Id;
    public Entity Identity(Entity entity) => entity;
    public bool Alive(Entity entity) => store.IsAlive(entity);
    public bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.HasValue<T>(entity);
    public T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.ReadValue<T>(entity);
    public void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.SetValue(entity, value);
    public void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.RemoveValue<T>(entity);
    public Entity Create(int ordinal) => store.Create();
    public Entity CreateEndpoint(int ordinal) => store.Create();
    public Entity CreateEdge(in ReferenceRelationEndpoints<Entity> endpoints, in SaveEdge payload)
    { Entity entity = store.Create(NativeFullArchiveWorld.EdgeColumns); store.SetValue(entity, endpoints); store.SetValue(entity, payload); return entity; }
    public void Destroy(Entity entity) => store.Destroy(entity);
    public void Dispose() { }
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceParent<Entity>>(entity)) result.Parent(entity, store.ReadValue<ReferenceParent<Entity>>(entity).Value);
            if (store.HasValue<ReferenceChildren<Entity>>(entity))
            { var children = store.ReadValue<ReferenceChildren<Entity>>(entity); result.Children(entity, children.Items.AsSpan(0, children.Count)); }
        }
    }
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceRelationEndpoints<Entity>>(entity))
            {
                var endpoints = store.ReadValue<ReferenceRelationEndpoints<Entity>>(entity); SaveEdge payload = store.ReadValue<SaveEdge>(entity);
                result.Main(entity, endpoints.Source, endpoints.Target, payload.Ordinal, payload.Value);
                if (endpoints.Added > since) result.Fact(entity, 1);
                if (endpoints.Written > since) result.Fact(entity, 2);
            }
            if (store.HasValue<ReferenceOldRelationEndpoints<Entity>>(entity))
            { var old = store.ReadValue<ReferenceOldRelationEndpoints<Entity>>(entity); if (old.Version > since) result.Removed(entity, old.Source, old.Target); }
        }
    }
    public int Id(Entity entity) => entity.Id;
    public int Resolve(Entity entity, bool bind)
    { if (!store.IsAlive(entity)) throw new ArgumentException("Entity generation has expired."); return entity.Id; }
    public bool Matches(Entity entity) => store.HasValue<Position>(entity) && store.HasValue<Team>(entity);
}

internal readonly struct DefaultArchiveValues(DefaultArchiveStore store) : INativeArchiveValues
{
    public NativeArchiveStore Store => store;
    public Entity Create(ulong componentTypes = 0) => store.Create(componentTypes);

    public int Index(Entity entity) => entity.Id;
    public Entity Identity(Entity entity) => entity;
    public bool Alive(Entity entity) => store.IsAlive(entity);
    public bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.HasValue<T>(entity);
    public T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.ReadValue<T>(entity);
    public void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.SetValue(entity, value);
    public void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.RemoveValue<T>(entity);
    public Entity Create(int ordinal) => store.Create();
    public Entity CreateEndpoint(int ordinal) => store.Create();
    public Entity CreateEdge(in ReferenceRelationEndpoints<Entity> endpoints, in SaveEdge payload)
    { Entity entity = store.Create(NativeFullArchiveWorld.EdgeColumns); store.SetValue(entity, endpoints); store.SetValue(entity, payload); return entity; }
    public void Destroy(Entity entity) => store.Destroy(entity);
    public void Dispose() { }
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceParent<Entity>>(entity)) result.Parent(entity, store.ReadValue<ReferenceParent<Entity>>(entity).Value);
            if (store.HasValue<ReferenceChildren<Entity>>(entity))
            { var children = store.ReadValue<ReferenceChildren<Entity>>(entity); result.Children(entity, children.Items.AsSpan(0, children.Count)); }
        }
    }
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.HasValue<ReferenceRelationEndpoints<Entity>>(entity))
            {
                var endpoints = store.ReadValue<ReferenceRelationEndpoints<Entity>>(entity); SaveEdge payload = store.ReadValue<SaveEdge>(entity);
                result.Main(entity, endpoints.Source, endpoints.Target, payload.Ordinal, payload.Value);
                if (endpoints.Added > since) result.Fact(entity, 1);
                if (endpoints.Written > since) result.Fact(entity, 2);
            }
            if (store.HasValue<ReferenceOldRelationEndpoints<Entity>>(entity))
            { var old = store.ReadValue<ReferenceOldRelationEndpoints<Entity>>(entity); if (old.Version > since) result.Removed(entity, old.Source, old.Target); }
        }
    }
    public int Id(Entity entity) => entity.Id;
    public int Resolve(Entity entity, bool bind)
    { if (!store.IsAlive(entity)) throw new ArgumentException("Entity generation has expired."); return entity.Id; }
    public bool Matches(Entity entity) => store.HasValue<Position>(entity) && store.HasValue<Team>(entity);
}
