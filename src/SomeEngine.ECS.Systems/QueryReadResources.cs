using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Read-only entity projection used to collect query component references.</summary>
public interface IQueryReadEntities
{
    int Count { get; }
    Entity GetEntity(int index);
}

/// <summary>Concrete component-resource descriptions held through a callback-scoped query read.</summary>
public sealed partial class QueryReadResources
{
    private readonly List<IComponentReadResources> _components = [];
    private readonly List<JobResourceAccess> _accesses = [];
    private readonly List<ComponentResourceUse> _uses = [];
    private readonly List<object> _references = [];

    public void Add<TComponent, TResources>()
        where TComponent : struct, IComponent
        where TResources : struct, IJobComponentResources<TComponent> =>
        AddComponent(new ComponentReadResources<TComponent, TResources>());

    /// <summary>Combines cold component-resource registrations for one consumer.</summary>
    public void AddFrom(QueryReadResources resources)
    {
        foreach (IComponentReadResources component in resources._components)
            AddComponent(component.Create());
    }

    private void AddComponent(IComponentReadResources component)
    {
        foreach (IComponentReadResources registered in _components)
            if (registered.GetType() == component.GetType()) return;
        _components.Add(component);
    }

    internal bool ContainsComponent(int componentId) =>
        _components.Exists(component => component.ComponentId == componentId);

    private static bool DeclaresRead(scoped QueryCursor query, int componentId)
    {
        foreach (QueryDataAccess access in query.State.Definition.DataAccesses)
            if (access.ComponentId == componentId && access.Access.CanRead()) return true;
        return false;
    }

    public void Execute<TState>(scoped QueryCursor query, ref TState state, QueryExecution<TState> execute)
        where TState : allows ref struct =>
        ExecuteCore(query, new SpanEntities(default), selectEntities: false, ref state, execute);

    /// <summary>Holds referenced resources for the supplied entities within the current query.</summary>
    public void ExecuteEntities<TState>(scoped QueryCursor query, scoped ReadOnlySpan<Entity> entities,
        ref TState state, QueryExecution<TState> execute)
        where TState : allows ref struct =>
        ExecuteEntities(query, new SpanEntities(entities), ref state, execute);

    public void ExecuteEntities<TState, TEntities>(scoped QueryCursor query, scoped TEntities entities,
        ref TState state, QueryExecution<TState> execute)
        where TState : allows ref struct
        where TEntities : IQueryReadEntities, allows ref struct =>
        ExecuteCore(query, entities, selectEntities: true, ref state, execute);

    private void ExecuteCore<TState, TEntities>(scoped QueryCursor query, scoped TEntities entities,
        bool selectEntities, ref TState state, QueryExecution<TState> execute)
        where TState : allows ref struct
        where TEntities : IQueryReadEntities, allows ref struct
    {
        if (_components.Count == 0)
        {
            execute(query, ref state);
            return;
        }
        Scheduler scheduler = WorldJobs.TryGetScheduler(query.Owner, out Scheduler? jobs) ? jobs : JobSystem.Current;
        while (true)
        {
            _accesses.Clear();
            _uses.Clear();
            _references.Clear();
            var resources = new JobResourceAccessBuilder(scheduler, _uses, _references);
            foreach (IComponentReadResources component in _components)
            {
                if (!DeclaresRead(query, component.ComponentId)) continue;
                if (selectEntities) component.CollectEntities(query, entities, ref resources);
                else component.Collect(query, ref resources);
            }
            foreach (ComponentResourceUse use in _uses) _accesses.Add(use.Access);
            SynchronousResourceOwner access = default;
            try
            {
                if (JobSystem.IsExecutingJob)
                    foreach (JobResourceAccess required in _accesses)
                        scheduler.RequireCurrentAccess(required, requireSingleWorkItem: false);
                else
                    access = scheduler.AcquireSynchronousAccess(CollectionsMarshal.AsSpan(_accesses));
                bool current = true;
                foreach (IComponentReadResources component in _components)
                {
                    if (!DeclaresRead(query, component.ComponentId)) continue;
                    current &= selectEntities ? component.EntityVersionsMatch(query, entities) : component.VersionsMatch(query);
                }
                if (!current)
                    continue;
                execute(query, ref state);
                return;
            }
            finally
            {
                access.Dispose();
                _uses.Clear();
                _references.Clear();
            }
        }
    }

    private interface IComponentReadResources
    {
        int ComponentId { get; }
        IComponentReadResources Create();
        void Collect(scoped QueryCursor query, ref JobResourceAccessBuilder resources);
        bool VersionsMatch(scoped QueryCursor query);
        void CollectEntities<TEntities>(scoped QueryCursor query, scoped TEntities entities,
            ref JobResourceAccessBuilder resources) where TEntities : IQueryReadEntities, allows ref struct;
        bool EntityVersionsMatch<TEntities>(scoped QueryCursor query, scoped TEntities entities)
            where TEntities : IQueryReadEntities, allows ref struct;
    }

    private sealed class ComponentReadResources<TComponent, TResources> : IComponentReadResources
        where TComponent : struct, IComponent
        where TResources : struct, IJobComponentResources<TComponent>
    {
        private readonly List<long> _versions = [];
        public int ComponentId => ComponentMetadata<TComponent>.Id;
        public IComponentReadResources Create() => new ComponentReadResources<TComponent, TResources>();

        public void CollectEntities<TEntities>(scoped QueryCursor query, scoped TEntities entities,
            ref JobResourceAccessBuilder resources) where TEntities : IQueryReadEntities, allows ref struct
        {
            _versions.Clear();
            for (int index = 0; index < entities.Count; index++)
            {
                if (!query.TryReadRef<TComponent>(entities.GetEntity(index), out var component)) continue;
                _versions.Add(TResources.GetVersion(in component.Value));
                resources.Retain(in component.Value);
                TResources.Collect(in component.Value, ref resources);
            }
        }

        public bool EntityVersionsMatch<TEntities>(scoped QueryCursor query, scoped TEntities entities)
            where TEntities : IQueryReadEntities, allows ref struct
        {
            int index = 0;
            for (int entityIndex = 0; entityIndex < entities.Count; entityIndex++)
                if (query.TryReadRef<TComponent>(entities.GetEntity(entityIndex), out var component))
                {
                    if (index == _versions.Count || _versions[index++] != TResources.GetVersion(in component.Value))
                        return false;
                }
            return index == _versions.Count;
        }

        public void Collect(scoped QueryCursor query, ref JobResourceAccessBuilder resources)
        {
            _versions.Clear();
            foreach (QueryChunkView chunk in query.Chunks)
            {
                if (!chunk.TryRead<TComponent>(out ReadOnlySpan<TComponent> values))
                    continue;
                foreach (int row in chunk.RowIndices)
                {
                    _versions.Add(TResources.GetVersion(in values[row]));
                    resources.Retain(in values[row]);
                    TResources.Collect(in values[row], ref resources);
                }
            }
        }

        public bool VersionsMatch(scoped QueryCursor query)
        {
            int index = 0;
            foreach (QueryChunkView chunk in query.Chunks)
            {
                if (!chunk.TryRead<TComponent>(out ReadOnlySpan<TComponent> values))
                    continue;
                foreach (int row in chunk.RowIndices)
                    if (index == _versions.Count || _versions[index++] != TResources.GetVersion(in values[row]))
                        return false;
            }
            return index == _versions.Count;
        }
    }

    private readonly ref struct SpanEntities(ReadOnlySpan<Entity> entities) : IQueryReadEntities
    {
        private readonly ReadOnlySpan<Entity> _entities = entities;
        public int Count => _entities.Length;
        public Entity GetEntity(int index) => _entities[index];
    }
}
