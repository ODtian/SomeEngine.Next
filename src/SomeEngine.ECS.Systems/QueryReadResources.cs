using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

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
        where TState : allows ref struct
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
                component.Collect(query, ref resources);
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
                    current &= component.VersionsMatch(query);
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
    }

    private sealed class ComponentReadResources<TComponent, TResources> : IComponentReadResources
        where TComponent : struct, IComponent
        where TResources : struct, IJobComponentResources<TComponent>
    {
        private readonly List<long> _versions = [];
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
}
