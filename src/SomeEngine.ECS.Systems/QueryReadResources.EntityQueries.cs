using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class QueryReadResources
{
    internal JobHandle ScheduleEntityQueries<TJob, TRebind>(World world, EntityQueryData[] queries,
        in TJob job, in TRebind rebind, ReadOnlySpan<JobResourceAccess> accesses, long topology, long structure)
        where TJob : struct, IJob where TRebind : struct, IJob
    {
        var workspaces = new ReadWorkspace[queries.Length];
        for (int index = 0; index < workspaces.Length; index++) workspaces[index] = TakeReadWorkspace();
        var request = new EntityReadRequest<TJob, TRebind>(this, world, queries, job, rebind, accesses.ToArray(),
            workspaces, topology, structure);
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        JobHandle handle = scheduler.Schedule(new CollectEntityResources<TJob, TRebind>(request), accesses);
        scheduler.OnCompleted(handle, static (_, state) => ((EntityReadRequest<TJob, TRebind>)state!).Release(), request);
        return handle;
    }

    private sealed class EntityReadRequest<TJob, TRebind>(QueryReadResources resources, World world,
        EntityQueryData[] queries, TJob job, TRebind rebind, JobResourceAccess[] reads, ReadWorkspace[] workspaces,
        long topology, long structure)
        where TJob : struct, IJob where TRebind : struct, IJob
    {
        private readonly List<JobResourceAccess> _current = [];

        private bool CheckTopology()
        {
            if (world.PublishedTopologyRevision == topology && world.PublishedStructureEpoch == structure) return true;
            rebind.Execute();
            return false;
        }

        private void CollectReferences()
        {
            _current.Clear();
            var iteration = world.ActiveStructureRoot.Iteration;
            iteration.BeginQueryBorrow();
            try
            {
                for (int index = 0; index < queries.Length; index++)
                {
                    EntityQueryData query = queries[index];
                    workspaces[index].Collect(WorldJobs.GetScheduler(world), new QueryCursor(in query.Data), new QueryEntities(query));
                    _current.AddRange(workspaces[index].Accesses);
                }
            }
            finally { iteration.EndQueryBorrow(); }
        }

        internal void Collect()
        {
            if (!CheckTopology()) return;
            CollectReferences();
            JobResourceAccess[] components = _current.ToArray();
            var accesses = new JobResourceAccess[reads.Length + components.Length];
            reads.CopyTo(accesses, 0);
            components.CopyTo(accesses, reads.Length);
            JobSystem.Schedule(new ExecuteEntityResources<TJob, TRebind>(this, components), accesses);
        }

        internal void Execute(JobResourceAccess[] components)
        {
            if (!CheckTopology()) return;
            bool current = true;
            for (int index = 0; index < queries.Length; index++)
            {
                EntityQueryData query = queries[index];
                current &= workspaces[index].VersionsMatch(new QueryCursor(in query.Data), new QueryEntities(query));
            }
            if (current)
            {
                CollectReferences();
                current = ComponentResourceGroups.AccessesMatch(components, CollectionsMarshal.AsSpan(_current));
            }
            if (current) job.Execute();
            else JobSystem.Schedule(new CollectEntityResources<TJob, TRebind>(this), reads);
        }

        internal void Release()
        {
            foreach (ReadWorkspace workspace in workspaces) resources.ReturnReadWorkspace(workspace);
            _current.Clear();
        }
    }

    private readonly struct QueryEntities(EntityQueryData query) : IQueryReadEntities
    {
        public int Count => query.Count;
        public Entity GetEntity(int index) => query.Entities[index];
    }

    private readonly struct CollectEntityResources<TJob, TRebind>(EntityReadRequest<TJob, TRebind> request) : IJob
        where TJob : struct, IJob where TRebind : struct, IJob
    {
        public void Execute() => request.Collect();
    }
    private readonly struct ExecuteEntityResources<TJob, TRebind>(EntityReadRequest<TJob, TRebind> request,
        JobResourceAccess[] components) : IJob
        where TJob : struct, IJob where TRebind : struct, IJob
    {
        public void Execute() => request.Execute(components);
    }
}
