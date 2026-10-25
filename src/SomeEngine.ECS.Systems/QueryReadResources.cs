using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Read-only entity projection used to collect query component references.</summary>
public interface IQueryReadEntities
{
    int Count { get; }
    Entity GetEntity(int index);
}

/// <summary>Concrete component-resource descriptions held through a callback-scoped query read.</summary>
public sealed class QueryReadResources
{
    private readonly List<IComponentReadResources> _components = [];
    private readonly List<JobResourceAccess> _accesses = [];

    public void Add<TComponent, TResources>()
        where TComponent : struct, IComponent
        where TResources : struct, IJobComponentResources<TComponent> =>
        _components.Add(new ComponentReadResources<TComponent, TResources>());

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
            var resources = new JobResourceAccessBuilder(scheduler, _accesses);
            foreach (IComponentReadResources component in _components)
                if (selectEntities) component.CollectEntities(query, entities, ref resources);
                else component.Collect(query, ref resources);
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
                    current &= selectEntities ? component.EntityVersionsMatch(query, entities) : component.VersionsMatch(query);
                if (!current)
                    continue;
                execute(query, ref state);
                return;
            }
            finally { access.Dispose(); }
        }
    }

    private interface IComponentReadResources
    {
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

        public void CollectEntities<TEntities>(scoped QueryCursor query, scoped TEntities entities,
            ref JobResourceAccessBuilder resources) where TEntities : IQueryReadEntities, allows ref struct
        {
            _versions.Clear();
            for (int index = 0; index < entities.Count; index++)
            {
                if (!query.TryReadRef<TComponent>(entities.GetEntity(index), out var component)) continue;
                _versions.Add(TResources.GetVersion(in component.Value));
                TResources.Collect(in component.Value, ref resources);
            }
        }

        public bool EntityVersionsMatch<TEntities>(scoped QueryCursor query, scoped TEntities entities)
            where TEntities : IQueryReadEntities, allows ref struct
        {
            int index = 0;
            for (int entityIndex = 0; entityIndex < entities.Count; entityIndex++)
                if (query.TryReadRef<TComponent>(entities.GetEntity(entityIndex), out var component) &&
                    _versions[index++] != TResources.GetVersion(in component.Value))
                    return false;
            return true;
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
                    if (_versions[index++] != TResources.GetVersion(in values[row]))
                        return false;
            }
            return true;
        }
    }

    private readonly ref struct SpanEntities(ReadOnlySpan<Entity> entities) : IQueryReadEntities
    {
        private readonly ReadOnlySpan<Entity> _entities = entities;
        public int Count => _entities.Length;
        public Entity GetEntity(int index) => _entities[index];
    }
}
