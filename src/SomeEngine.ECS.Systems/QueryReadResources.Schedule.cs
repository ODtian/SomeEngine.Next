using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class QueryReadResources
{
    private readonly Lock _scheduledReadsGate = new();
    private readonly Stack<ReadWorkspace> _scheduledReads = [];

    /// <summary>Binds current participating component references after the dependency, then executes the Job once.</summary>
    public JobHandle ScheduleEntities<TJob, TEntities>(World world, QueryHandle query, TEntities entities,
        in TJob job, ReadOnlySpan<JobResourceAccess> accesses, JobHandle dependency = default)
        where TJob : struct, IJob
        where TEntities : IQueryReadEntities
    {
        QueryDefinition definition = world.GetQueryDefinition(query);
        var reads = new JobResourceAccess[QueryJobAccess.Count(definition) + accesses.Length];
        int count = QueryJobAccess.CopyAccesses(world, definition, reads, this);
        accesses.CopyTo(reads.AsSpan(count));
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        ReadWorkspace workspace = TakeReadWorkspace();
        var request = new ReadRequest<TJob, TEntities>(this, workspace, world, query, entities, job, reads);
        try
        {
            JobHandle handle = scheduler.Schedule(new CollectReadResources<TJob, TEntities>(request), reads, dependency);
            scheduler.OnCompleted(handle, static (_, state) => ((ReadRequest<TJob, TEntities>)state!).Release(), request);
            return handle;
        }
        catch
        {
            request.Release();
            throw;
        }
    }

    private ReadWorkspace TakeReadWorkspace()
    {
        ReadWorkspace workspace;
        lock (_scheduledReadsGate) workspace = _scheduledReads.Count == 0 ? new() : _scheduledReads.Pop();
        if (workspace.Components.Length != _components.Count)
            workspace.Components = _components.Select(static component => component.Create()).ToArray();
        return workspace;
    }

    private void ReturnReadWorkspace(ReadWorkspace workspace)
    {
        workspace.Uses.Clear();
        workspace.References.Clear();
        workspace.Accesses.Clear();
        lock (_scheduledReadsGate) _scheduledReads.Push(workspace);
    }

    private sealed class ReadWorkspace
    {
        internal IComponentReadResources[] Components = [];
        internal readonly List<ComponentResourceUse> Uses = [];
        internal readonly List<object> References = [];
        internal readonly List<JobResourceAccess> Accesses = [];

        internal void Collect<TEntities>(Scheduler scheduler, scoped QueryCursor query, TEntities entities)
            where TEntities : IQueryReadEntities
        {
            Uses.Clear();
            Accesses.Clear();
            var resources = new JobResourceAccessBuilder(scheduler, Uses, References);
            foreach (IComponentReadResources component in Components)
                if (DeclaresRead(query, component.ComponentId)) component.CollectEntities(query, entities, ref resources);
            foreach (ComponentResourceUse use in Uses)
            {
                Accesses.Add(use.Access);
                References.Add(use.Data);
            }
        }

        internal bool VersionsMatch<TEntities>(scoped QueryCursor query, TEntities entities)
            where TEntities : IQueryReadEntities
        {
            foreach (IComponentReadResources component in Components)
                if (DeclaresRead(query, component.ComponentId) && !component.EntityVersionsMatch(query, entities)) return false;
            return true;
        }
    }

    private sealed class ReadRequest<TJob, TEntities>(QueryReadResources resources, ReadWorkspace workspace,
        World world, QueryHandle query, TEntities entities, TJob job, JobResourceAccess[] reads)
        where TJob : struct, IJob
        where TEntities : IQueryReadEntities
    {
        internal World World => world;
        internal QueryHandle Query => query;
        internal Scheduler Scheduler => WorldJobs.GetScheduler(world);

        internal void Collect(scoped QueryCursor view)
        {
            workspace.Collect(Scheduler, view, entities);
            JobResourceAccess[] componentAccesses = workspace.Accesses.ToArray();
            var accesses = new JobResourceAccess[reads.Length + componentAccesses.Length];
            reads.CopyTo(accesses, 0);
            componentAccesses.CopyTo(accesses, reads.Length);
            JobSystem.Schedule(new ExecuteReadResources<TJob, TEntities>(this, componentAccesses), accesses);
        }

        internal bool ResourcesMatch(scoped QueryCursor view, JobResourceAccess[] componentAccesses)
        {
            bool current = workspace.VersionsMatch(view, entities);
            if (current)
            {
                workspace.Collect(Scheduler, view, entities);
                current = ComponentResourceGroups.AccessesMatch(componentAccesses, CollectionsMarshal.AsSpan(workspace.Accesses));
            }
            return current;
        }

        internal void Execute(bool current)
        {
            if (current) job.Execute();
            else JobSystem.Schedule(new CollectReadResources<TJob, TEntities>(this), reads);
        }

        internal void Release()
        {
            resources.ReturnReadWorkspace(workspace);
        }
    }

    private readonly struct CollectReadResources<TJob, TEntities>(ReadRequest<TJob, TEntities> request) : IJob
        where TJob : struct, IJob
        where TEntities : IQueryReadEntities
    {
        public void Execute()
        {
            var work = request;
            request.World.ExecuteReadQuery(request.Query, ref work,
                static (scoped QueryCursor query, ref ReadRequest<TJob, TEntities> state) => state.Collect(query));
        }
    }

    private struct ExecuteReadResources<TJob, TEntities>(ReadRequest<TJob, TEntities> request,
        JobResourceAccess[] componentAccesses) : IJob
        where TJob : struct, IJob
        where TEntities : IQueryReadEntities
    {
        private bool _current;

        public void Execute()
        {
            var work = this;
            request.World.ExecuteReadQuery(request.Query, ref work,
                static (scoped QueryCursor query, ref ExecuteReadResources<TJob, TEntities> state) => state.Read(query));
            request.Execute(work._current);
        }

        private void Read(scoped QueryCursor query) => _current = request.ResourcesMatch(query, componentAccesses);
    }
}
