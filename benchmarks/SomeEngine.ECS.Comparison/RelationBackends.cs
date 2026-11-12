using Friflo.Engine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal struct EasyObserveRelations<TPayload>(RelationObservation<EasyEcs.Entity> result, ulong since)
    : EasyEcs.IForEachBatch<ReferenceRelationEndpoints<EasyEcs.Entity>, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{
    public static int Writes => 0;
    public void Execute(EasyEcs.EntitySpan entities, Span<ReferenceRelationEndpoints<EasyEcs.Entity>> endpoints, Span<TPayload> payloads)
    {
        for (int i = 0; i < endpoints.Length; i++)
        {
            var value = endpoints[i]; var edge = entities[i];
            result.Main(edge, value.Source, value.Target, payloads[i].Ordinal, payloads[i].Value);
            if (value.Added > since) result.Fact(edge, 1);
            if (value.Written > since) result.Fact(edge, 2);
        }
    }
}
internal struct EasyObserveOldRelations(RelationObservation<EasyEcs.Entity> result, ulong since)
    : EasyEcs.IForEachBatch<ReferenceOldRelationEndpoints<EasyEcs.Entity>>
{
    public static int Writes => 0;
    public void Execute(EasyEcs.EntitySpan entities, Span<ReferenceOldRelationEndpoints<EasyEcs.Entity>> endpoints)
    {
        for (int i = 0; i < endpoints.Length; i++)
            if (endpoints[i].Version > since) result.Removed(entities[i], endpoints[i].Source, endpoints[i].Target);
    }
}

internal readonly struct EasyRelationValues<TPayload>(EasyEcs.World world) : IRelationValues<EasyEcs.Entity, int, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{

    public int Index(EasyEcs.Entity entity) => entity.Id.Index;
    public int Identity(EasyEcs.Entity entity) => unchecked((int)entity.Id.Version);
    public bool Alive(EasyEcs.Entity entity) => entity.IsAlive;
    public bool Has<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Read<T>();
    public void Set<T>(EasyEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Remove<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Remove<T>();
    public void Destroy(EasyEcs.Entity entity) => entity.Destroy();
    public EasyEcs.Entity CreateEdge(in ReferenceRelationEndpoints<EasyEcs.Entity> endpoints, in TPayload payload)
    {
        return world.CreateEntity(endpoints, payload);
    }
    public EasyEcs.Entity CreateEndpoint(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.CreateEntity(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.CreateEntity(p, v, h, new Team()) : world.CreateEntity(p, v, h);
    }
    public void Observe(RelationObservation<EasyEcs.Entity> result, ulong since)
    {
        var main = new EasyObserveRelations<TPayload>(result, since);
        world.ForEachBatch<ReferenceRelationEndpoints<EasyEcs.Entity>, TPayload, EasyObserveRelations<TPayload>>(ref main);
        var old = new EasyObserveOldRelations(result, since);
        world.ForEachBatch<ReferenceOldRelationEndpoints<EasyEcs.Entity>, EasyObserveOldRelations>(ref old);
    }
    public void Dispose() {  }
}

internal readonly struct FrifloRelationValues<TPayload>(EntityStore world) : IRelationValues<Friflo.Engine.ECS.Entity, int, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{
    private readonly ArchetypeQuery<ReferenceRelationEndpoints<Friflo.Engine.ECS.Entity>, TPayload> _main = world.Query<ReferenceRelationEndpoints<Friflo.Engine.ECS.Entity>, TPayload>();
    private readonly ArchetypeQuery<ReferenceOldRelationEndpoints<Friflo.Engine.ECS.Entity>> _old = world.Query<ReferenceOldRelationEndpoints<Friflo.Engine.ECS.Entity>>();
    public int Index(Friflo.Engine.ECS.Entity entity) => entity.Id;
    public int Identity(Friflo.Engine.ECS.Entity entity) => entity.Revision;
    public bool Alive(Friflo.Engine.ECS.Entity entity) => !entity.IsNull;
    public bool Has<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.HasComponent<T>();
    public T Read<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.GetComponent<T>();
    public void Set<T>(Friflo.Engine.ECS.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.AddComponent(value); }
    public void Remove<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.RemoveComponent<T>();
    public void Destroy(Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
    public Friflo.Engine.ECS.Entity CreateEdge(in ReferenceRelationEndpoints<Friflo.Engine.ECS.Entity> endpoints, in TPayload payload)
    {
        return world.CreateEntity(endpoints, payload);
    }
    public Friflo.Engine.ECS.Entity CreateEndpoint(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.CreateEntity(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.CreateEntity(p, v, h, new Team()) : world.CreateEntity(p, v, h);
    }
    public void Observe(RelationObservation<Friflo.Engine.ECS.Entity> result, ulong since)
    {
        foreach (var (endpoints, payloads, entities) in _main.Chunks)
        {
            var values = endpoints.Span; var data = payloads.Span;
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i]; var edge = entities.EntityAt(i);
                result.Main(edge, value.Source, value.Target, data[i].Ordinal, data[i].Value);
                if (value.Added > since) result.Fact(edge, 1);
                if (value.Written > since) result.Fact(edge, 2);
            }
        }
        foreach (var (endpoints, entities) in _old.Chunks)
        {
            var values = endpoints.Span;
            for (int i = 0; i < values.Length; i++)
                if (values[i].Version > since) result.Removed(entities.EntityAt(i), values[i].Source, values[i].Target);
        }
    }
    public void Dispose() {  }
}

internal readonly struct ArchRelationValues<TPayload>(Arch.Core.World world) : IRelationValues<Arch.Core.Entity, int, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{
    private readonly Arch.Core.QueryDescription _main = new Arch.Core.QueryDescription().WithAll<ReferenceRelationEndpoints<Arch.Core.Entity>, TPayload>();
    private readonly Arch.Core.QueryDescription _old = new Arch.Core.QueryDescription().WithAll<ReferenceOldRelationEndpoints<Arch.Core.Entity>>();
    public int Index(Arch.Core.Entity entity) => entity.Id;
    public int Identity(Arch.Core.Entity entity) => entity.Version;
    public bool Alive(Arch.Core.Entity entity) => world.IsAlive(entity);
    public bool Has<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Has<T>(entity);
    public T Read<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Get<T>(entity);
    public void Set<T>(Arch.Core.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { if (world.Has<T>(entity)) world.Set(entity, value); else world.Add(entity, value); }
    public void Remove<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Remove<T>(entity);
    public void Destroy(Arch.Core.Entity entity) => world.Destroy(entity);
    public Arch.Core.Entity CreateEdge(in ReferenceRelationEndpoints<Arch.Core.Entity> endpoints, in TPayload payload)
    {
        return world.Create(endpoints, payload);
    }
    public Arch.Core.Entity CreateEndpoint(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        return ordinal % 4 == 0 ? world.Create(p, v, h, new Team(), new Sleeping())
            : ordinal % 2 == 0 ? world.Create(p, v, h, new Team()) : world.Create(p, v, h);
    }
    public void Observe(RelationObservation<Arch.Core.Entity> result, ulong since)
    {
        foreach (ref var chunk in world.Query(in _main).GetChunkIterator())
        {
            var values = chunk.GetSpan<ReferenceRelationEndpoints<Arch.Core.Entity>>(); var data = chunk.GetSpan<TPayload>(); var entities = chunk.Entities;
            for (int i = 0; i < chunk.Count; i++)
            {
                var value = values[i]; var edge = entities[i];
                result.Main(edge, value.Source, value.Target, data[i].Ordinal, data[i].Value);
                if (value.Added > since) result.Fact(edge, 1);
                if (value.Written > since) result.Fact(edge, 2);
            }
        }
        foreach (ref var chunk in world.Query(in _old).GetChunkIterator())
        {
            var values = chunk.GetSpan<ReferenceOldRelationEndpoints<Arch.Core.Entity>>(); var entities = chunk.Entities;
            for (int i = 0; i < chunk.Count; i++)
                if (values[i].Version > since) result.Removed(entities[i], values[i].Source, values[i].Target);
        }
    }
    public void Dispose() {  }
}

internal readonly struct DefaultRelationValues<TPayload>(DefaultEcs.World world) : IRelationValues<DefaultEcs.Entity, DefaultEcs.Entity, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{
    private readonly DefaultEcs.EntitySet _main = world.GetEntities().With<ReferenceRelationEndpoints<DefaultEcs.Entity>>().With<TPayload>().AsSet();
    private readonly DefaultEcs.EntitySet _old = world.GetEntities().With<ReferenceOldRelationEndpoints<DefaultEcs.Entity>>().AsSet();
    public int Index(DefaultEcs.Entity entity) => entity.GetHashCode();
    public DefaultEcs.Entity Identity(DefaultEcs.Entity entity) => entity;
    public bool Alive(DefaultEcs.Entity entity) => entity.IsAlive;
    public bool Has<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Get<T>();
    public void Set<T>(DefaultEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Remove<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Remove<T>();
    public void Destroy(DefaultEcs.Entity entity) => entity.Dispose();
    public DefaultEcs.Entity CreateEdge(in ReferenceRelationEndpoints<DefaultEcs.Entity> endpoints, in TPayload payload)
    {
        var edge = world.CreateEntity(); edge.Set(endpoints); edge.Set(payload); return edge;
    }
    public DefaultEcs.Entity CreateEndpoint(int ordinal)
    {
        var p = new Position { X = ordinal }; var v = new Velocity { X = 1, Y = 2 }; var h = new Health { Value = 100, Max = 100 };
        var entity = world.CreateEntity(); entity.Set(p); entity.Set(v); entity.Set(h);
        if (ordinal % 2 == 0) entity.Set(new Team()); if (ordinal % 4 == 0) entity.Set(new Sleeping()); return entity;
    }
    public void Observe(RelationObservation<DefaultEcs.Entity> result, ulong since)
    {
        foreach (ref readonly var edge in _main.GetEntities())
        {
            ref var value = ref edge.Get<ReferenceRelationEndpoints<DefaultEcs.Entity>>(); ref var payload = ref edge.Get<TPayload>();
            result.Main(edge, value.Source, value.Target, payload.Ordinal, payload.Value);
            if (value.Added > since) result.Fact(edge, 1);
            if (value.Written > since) result.Fact(edge, 2);
        }
        foreach (ref readonly var edge in _old.GetEntities())
        {
            ref var value = ref edge.Get<ReferenceOldRelationEndpoints<DefaultEcs.Entity>>();
            if (value.Version > since) result.Removed(edge, value.Source, value.Target);
        }
    }
    public void Dispose() { _main.Dispose(); _old.Dispose(); }
}

internal sealed partial class SomeEngineWorld
{
    private void PrepareRelations(string workload)
    {
        if (RelationScenario is not null) return;
        bool ordered = workload.EndsWith("-ordered");
        RelationScenario = (ordered ? workload[..^8] : workload) switch
        {
            "relation-directed-parallel" => CreateRelations<DirectedParallelPayload>(ordered),
            "relation-directed-unique-pair" => CreateRelations<DirectedUniquePairPayload>(ordered),
            "relation-directed-unique-source" => CreateRelations<DirectedUniqueSourcePayload>(ordered),
            "relation-directed-unique-target" => CreateRelations<DirectedUniqueTargetPayload>(ordered),
            "relation-directed-one-to-one" => CreateRelations<DirectedOneToOnePayload>(ordered),
            "relation-undirected-parallel" => CreateRelations<UndirectedParallelPayload>(ordered),
            "relation-undirected-unique-pair" => CreateRelations<UndirectedUniquePairPayload>(ordered),
            "relation-undirected-one-to-one" => CreateRelations<UndirectedOneToOnePayload>(ordered),
            _ => throw new ArgumentException(workload),
        };
    }
    private IRelationScenario CreateRelations<TPayload>(bool ordered) where TPayload : struct, IRelationPayload<TPayload>
    {
        RelationSchema schema = RelationSchema.For<TPayload>();
        var api = new SomeRelationComparison<TPayload>(_world, Count);
        return new RelationScenario<SomeEngine.ECS.Entities.Entity, SomeRelationComparison<TPayload>>(api, _entities,
            schema.Direction == RelationDirection.Directed, schema.Direction == RelationDirection.Undirected && schema.Cardinality == RelationCardinality.OneToOne, ordered);
    }
}

internal sealed partial class EasyEcsWorld
{
    private void PrepareRelations(string workload)
    {
        if (RelationScenario is not null) return;
        bool ordered = workload.EndsWith("-ordered");
        RelationScenario = (ordered ? workload[..^8] : workload) switch
        {
            "relation-directed-parallel" => CreateRelations<DirectedParallelPayload>(ordered),
            "relation-directed-unique-pair" => CreateRelations<DirectedUniquePairPayload>(ordered),
            "relation-directed-unique-source" => CreateRelations<DirectedUniqueSourcePayload>(ordered),
            "relation-directed-unique-target" => CreateRelations<DirectedUniqueTargetPayload>(ordered),
            "relation-directed-one-to-one" => CreateRelations<DirectedOneToOnePayload>(ordered),
            "relation-undirected-parallel" => CreateRelations<UndirectedParallelPayload>(ordered),
            "relation-undirected-unique-pair" => CreateRelations<UndirectedUniquePairPayload>(ordered),
            "relation-undirected-one-to-one" => CreateRelations<UndirectedOneToOnePayload>(ordered),
            _ => throw new ArgumentException(workload),
        };
    }
    private IRelationScenario CreateRelations<TPayload>(bool ordered) where TPayload : struct, IRelationPayload<TPayload>
    {
        RelationSchema schema = RelationSchema.For<TPayload>();
        var api = new ReferenceRelations<EasyEcs.Entity, int, EasyRelationValues<TPayload>, TPayload>(new(_world), Count * 3 + Changes + 1);
        return new RelationScenario<EasyEcs.Entity, ReferenceRelations<EasyEcs.Entity, int, EasyRelationValues<TPayload>, TPayload>>(api, _entities,
            schema.Direction == RelationDirection.Directed, schema.Direction == RelationDirection.Undirected && schema.Cardinality == RelationCardinality.OneToOne, ordered);
    }
}

internal sealed partial class FrifloWorld
{
    private void PrepareRelations(string workload)
    {
        if (RelationScenario is not null) return;
        bool ordered = workload.EndsWith("-ordered");
        RelationScenario = (ordered ? workload[..^8] : workload) switch
        {
            "relation-directed-parallel" => CreateRelations<DirectedParallelPayload>(ordered),
            "relation-directed-unique-pair" => CreateRelations<DirectedUniquePairPayload>(ordered),
            "relation-directed-unique-source" => CreateRelations<DirectedUniqueSourcePayload>(ordered),
            "relation-directed-unique-target" => CreateRelations<DirectedUniqueTargetPayload>(ordered),
            "relation-directed-one-to-one" => CreateRelations<DirectedOneToOnePayload>(ordered),
            "relation-undirected-parallel" => CreateRelations<UndirectedParallelPayload>(ordered),
            "relation-undirected-unique-pair" => CreateRelations<UndirectedUniquePairPayload>(ordered),
            "relation-undirected-one-to-one" => CreateRelations<UndirectedOneToOnePayload>(ordered),
            _ => throw new ArgumentException(workload),
        };
    }
    private IRelationScenario CreateRelations<TPayload>(bool ordered) where TPayload : struct, IRelationPayload<TPayload>
    {
        RelationSchema schema = RelationSchema.For<TPayload>();
        var api = new ReferenceRelations<Friflo.Engine.ECS.Entity, int, FrifloRelationValues<TPayload>, TPayload>(new(_world), Count * 3 + Changes + 1);
        return new RelationScenario<Friflo.Engine.ECS.Entity, ReferenceRelations<Friflo.Engine.ECS.Entity, int, FrifloRelationValues<TPayload>, TPayload>>(api, _entities,
            schema.Direction == RelationDirection.Directed, schema.Direction == RelationDirection.Undirected && schema.Cardinality == RelationCardinality.OneToOne, ordered);
    }
}

internal sealed partial class ArchWorld
{
    private void PrepareRelations(string workload)
    {
        if (RelationScenario is not null) return;
        bool ordered = workload.EndsWith("-ordered");
        RelationScenario = (ordered ? workload[..^8] : workload) switch
        {
            "relation-directed-parallel" => CreateRelations<DirectedParallelPayload>(ordered),
            "relation-directed-unique-pair" => CreateRelations<DirectedUniquePairPayload>(ordered),
            "relation-directed-unique-source" => CreateRelations<DirectedUniqueSourcePayload>(ordered),
            "relation-directed-unique-target" => CreateRelations<DirectedUniqueTargetPayload>(ordered),
            "relation-directed-one-to-one" => CreateRelations<DirectedOneToOnePayload>(ordered),
            "relation-undirected-parallel" => CreateRelations<UndirectedParallelPayload>(ordered),
            "relation-undirected-unique-pair" => CreateRelations<UndirectedUniquePairPayload>(ordered),
            "relation-undirected-one-to-one" => CreateRelations<UndirectedOneToOnePayload>(ordered),
            _ => throw new ArgumentException(workload),
        };
    }
    private IRelationScenario CreateRelations<TPayload>(bool ordered) where TPayload : struct, IRelationPayload<TPayload>
    {
        RelationSchema schema = RelationSchema.For<TPayload>();
        var api = new ReferenceRelations<Arch.Core.Entity, int, ArchRelationValues<TPayload>, TPayload>(new(_world), Count * 3 + Changes + 1);
        return new RelationScenario<Arch.Core.Entity, ReferenceRelations<Arch.Core.Entity, int, ArchRelationValues<TPayload>, TPayload>>(api, _entities,
            schema.Direction == RelationDirection.Directed, schema.Direction == RelationDirection.Undirected && schema.Cardinality == RelationCardinality.OneToOne, ordered);
    }
}

internal sealed partial class DefaultEcsWorld
{
    private void PrepareRelations(string workload)
    {
        if (RelationScenario is not null) return;
        bool ordered = workload.EndsWith("-ordered");
        RelationScenario = (ordered ? workload[..^8] : workload) switch
        {
            "relation-directed-parallel" => CreateRelations<DirectedParallelPayload>(ordered),
            "relation-directed-unique-pair" => CreateRelations<DirectedUniquePairPayload>(ordered),
            "relation-directed-unique-source" => CreateRelations<DirectedUniqueSourcePayload>(ordered),
            "relation-directed-unique-target" => CreateRelations<DirectedUniqueTargetPayload>(ordered),
            "relation-directed-one-to-one" => CreateRelations<DirectedOneToOnePayload>(ordered),
            "relation-undirected-parallel" => CreateRelations<UndirectedParallelPayload>(ordered),
            "relation-undirected-unique-pair" => CreateRelations<UndirectedUniquePairPayload>(ordered),
            "relation-undirected-one-to-one" => CreateRelations<UndirectedOneToOnePayload>(ordered),
            _ => throw new ArgumentException(workload),
        };
    }
    private IRelationScenario CreateRelations<TPayload>(bool ordered) where TPayload : struct, IRelationPayload<TPayload>
    {
        RelationSchema schema = RelationSchema.For<TPayload>();
        var api = new ReferenceRelations<DefaultEcs.Entity, DefaultEcs.Entity, DefaultRelationValues<TPayload>, TPayload>(new(_world), Count * 3 + Changes + 1);
        return new RelationScenario<DefaultEcs.Entity, ReferenceRelations<DefaultEcs.Entity, DefaultEcs.Entity, DefaultRelationValues<TPayload>, TPayload>>(api, _entities,
            schema.Direction == RelationDirection.Directed, schema.Direction == RelationDirection.Undirected && schema.Cardinality == RelationCardinality.OneToOne, ordered);
    }
}
