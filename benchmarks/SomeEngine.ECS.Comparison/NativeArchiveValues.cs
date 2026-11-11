using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal readonly struct NativeArchiveValues(NativeArchiveStore store) : IHierarchyValues<Entity>,
    IRelationValues<Entity, Entity, SaveEdge>, IReferenceSparseAccess<Entity>
{
    public int Index(Entity entity) => entity.Id;
    public Entity Identity(Entity entity) => entity;
    public bool Alive(Entity entity) => store.IsAlive(entity);
    public bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.Has<T>(entity);
    public T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.Read<T>(entity);
    public void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.Set(entity, value);
    public void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => store.Remove<T>(entity);
    public Entity Create(int ordinal) => store.Create();
    public Entity CreateEndpoint(int ordinal) => store.Create();
    public Entity CreateEdge(in ReferenceRelationEndpoints<Entity> endpoints, in SaveEdge payload)
    { Entity entity = store.Create(NativeFullArchiveWorld.EdgeColumns); store.Set(entity, endpoints); store.Set(entity, payload); return entity; }
    public void Destroy(Entity entity) => store.Destroy(entity);
    public void Dispose() { }
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.Has<ReferenceParent<Entity>>(entity)) result.Parent(entity, store.Read<ReferenceParent<Entity>>(entity).Value);
            if (store.Has<ReferenceChildren<Entity>>(entity))
            { var children = store.Read<ReferenceChildren<Entity>>(entity); result.Children(entity, children.Items.AsSpan(0, children.Count)); }
        }
    }
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        for (int id = 1; id <= store.Used; id++) if (store.AliveIds[id])
        {
            Entity entity = new(id, store.Generations[id]);
            if (store.Has<ReferenceRelationEndpoints<Entity>>(entity))
            {
                var endpoints = store.Read<ReferenceRelationEndpoints<Entity>>(entity); SaveEdge payload = store.Read<SaveEdge>(entity);
                result.Main(entity, endpoints.Source, endpoints.Target, payload.Ordinal, payload.Value);
                if (endpoints.Added > since) result.Fact(entity, 1);
                if (endpoints.Written > since) result.Fact(entity, 2);
            }
            if (store.Has<ReferenceOldRelationEndpoints<Entity>>(entity))
            { var old = store.Read<ReferenceOldRelationEndpoints<Entity>>(entity); if (old.Version > since) result.Removed(entity, old.Source, old.Target); }
        }
    }
    public int Id(Entity entity) => entity.Id;
    public int Resolve(Entity entity, bool bind)
    { if (!store.IsAlive(entity)) throw new ArgumentException("Entity generation has expired."); return entity.Id; }
    public bool Matches(Entity entity) => store.Has<Position>(entity) && store.Has<Team>(entity);
}
