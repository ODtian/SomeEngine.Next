using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public static partial class EntityQueryJobs
{
    /// <summary>Holds the union of actual work ranges through a synchronous group callback and its joined ranges.</summary>
    public static JobHandle ScheduleGroup<TWork, TJob>(World world, in TWork work, in TJob job,
        ReadOnlySpan<JobResourceAccess> accesses = default, QueryReadResources? resources = null, JobHandle dependency = default)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryGroupJob
    {
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        Workspaces available = s_workspaces.GetValue(world, static _ => new());
        Workspace workspace;
        lock (available.Gate) workspace = available.Items.Count == 0 ? new() : available.Items.Pop();
        workspace.Reset();
        var request = new GroupRequest<TWork, TJob>(world, work, job, accesses.ToArray(), resources, workspace);
        JobHandle handle = scheduler.Schedule(new CaptureGroup<TWork, TJob>(request), request.CaptureAccesses, dependency);
        scheduler.OnCompleted(handle, static (_, state) =>
        {
            var done = ((Workspaces Available, Workspace Workspace))state!;
            lock (done.Available.Gate) done.Available.Items.Push(done.Workspace);
        }, (available, workspace));
        return handle;
    }

    private sealed class GroupRequest<TWork, TJob>(World world, TWork work, TJob job, JobResourceAccess[] external,
        QueryReadResources? resources, Workspace workspace)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryGroupJob
    {
        internal readonly JobResourceAccess[] CaptureAccesses = [RelationshipJobAccess.TopologyRead(world), .. external];
        private long _topology, _structure;

        internal void Capture()
        {
            _topology = world.PublishedTopologyRevision;
            _structure = world.PublishedStructureEpoch;
            int count = work.Count, queryCount = work.QueryCount;
            if (workspace.GroupQueries.Length != count) Array.Resize(ref workspace.GroupQueries, count);
            if (workspace.AllQueries.Length != count * queryCount) Array.Resize(ref workspace.AllQueries, count * queryCount);
            workspace.GroupAccesses.Clear();
            workspace.GroupAccesses.AddRange(CaptureAccesses);
            for (int index = 0; index < count; index++)
            {
                Batch batch = CaptureQueries(world, in work, index, workspace, index, resources);
                workspace.GroupQueries[index] = batch.Queries;
                batch.Queries.CopyTo(workspace.AllQueries, index * queryCount);
                workspace.GroupAccesses.AddRange(batch.Accesses);
            }
            var run = new RunGroup<TWork, TJob>(this);
            if (resources is null) JobSystem.Schedule(run, CollectionsMarshal.AsSpan(workspace.GroupAccesses));
            else resources.ScheduleEntityQueries(world, workspace.AllQueries, in run, new CaptureGroup<TWork, TJob>(this),
                CollectionsMarshal.AsSpan(workspace.GroupAccesses), _topology, _structure);
        }

        internal void Execute()
        {
            if (world.PublishedTopologyRevision != _topology || world.PublishedStructureEpoch != _structure)
            {
                JobSystem.Schedule(new CaptureGroup<TWork, TJob>(this), CaptureAccesses);
                return;
            }
            uint version = SetVersion(world, workspace, workspace.AllQueries);
            var iteration = world.ActiveStructureRoot.Iteration;
            bool completed;
            iteration.BeginQueryBorrow();
            try
            {
                TJob local = job;
                completed = local.Execute(new(world, workspace.GroupQueries, resources, version));
            }
            finally { iteration.EndQueryBorrow(); }
            if (!completed) JobSystem.Schedule(new CaptureGroup<TWork, TJob>(this), CaptureAccesses);
        }
    }
    private readonly struct CaptureGroup<TWork, TJob>(GroupRequest<TWork, TJob> request) : IJob
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryGroupJob
    {
        public void Execute() => request.Capture();
    }
    private readonly struct RunGroup<TWork, TJob>(GroupRequest<TWork, TJob> request) : IJob
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryGroupJob
    {
        public void Execute() => request.Execute();
    }

    internal static void ExecuteGroupQueries<TJob>(World world, EntityQueryData[][] queries, QueryReadResources? resources,
        in TJob job, int start, int count) where TJob : struct, IEntityQueryJob
    {
        Workspaces available = s_workspaces.GetValue(world, static _ => new());
        Workspace workspace;
        lock (available.Gate) workspace = available.Items.Count == 0 ? new() : available.Items.Pop();
        try
        {
            for (int index = 0; index < count; index++)
            {
                Batch batch = workspace.Get(index, 0);
                foreach (EntityQueryData query in queries[start + index]) batch.AddRanges(world, query, resources);
            }
            ExecuteCompatible(world, resources, workspace, queries, start, start, count, in job);
        }
        finally { lock (available.Gate) available.Items.Push(workspace); }
    }

    internal static void ExecuteGroupWork<TWork, TJob>(World world, QueryReadResources? resources, uint version,
        in TWork work, in TJob job, int start, int count)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        Workspaces available = s_workspaces.GetValue(world, static _ => new());
        Workspace workspace;
        lock (available.Gate) workspace = available.Items.Count == 0 ? new() : available.Items.Pop();
        try
        {
            if (workspace.GroupQueries.Length != count) Array.Resize(ref workspace.GroupQueries, count);
            for (int index = 0; index < count; index++)
            {
                Batch batch = CaptureQueries(world, in work, start + index, workspace, index, resources);
                SetVersion(world, version, batch.Queries);
                workspace.GroupQueries[index] = batch.Queries;
            }
            ExecuteCompatible(world, resources, workspace, workspace.GroupQueries, 0, start, count, in job);
        }
        finally { lock (available.Gate) available.Items.Push(workspace); }
    }

    private static void ExecuteCompatible<TJob>(World world, QueryReadResources? resources, Workspace workspace,
        EntityQueryData[][] queries, int queryStart, int workStart, int count, in TJob job) where TJob : struct, IEntityQueryJob
    {
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        scheduler.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world), requireSingleWorkItem: false);
        GroupRanges ranges = workspace.ParallelRanges;
        ranges.CurrentAccesses.Clear();
        ranges.CurrentStarts.Clear();
        ranges.CurrentStarts.Add(0);
        for (int index = 0; index < count; index++)
        {
            Batch batch = workspace.Batches[index];
            resources?.AddEntityQueryResources(world, queries[queryStart + index], batch.Accesses);
            foreach (JobResourceAccess access in batch.Accesses)
                scheduler.RequireCurrentAccess(access, requireSingleWorkItem: false);
            ranges.CurrentAccesses.AddRange(batch.Accesses);
            ranges.CurrentStarts.Add(ranges.CurrentAccesses.Count);
        }
        ranges.Update(count);
        scheduler.ExecuteParallel(new ExecuteCompatibleGroups<TJob>(queries, ranges.Groups, job, queryStart, workStart), ranges.Groups.Length);
    }

    private sealed class GroupRanges
    {
        internal readonly List<JobResourceAccess> CurrentAccesses = [], Accesses = [];
        internal readonly List<int> CurrentStarts = [], Starts = [];
        internal int[][] Groups = [];

        internal void Update(int count)
        {
            if (CollectionsMarshal.AsSpan(Starts).SequenceEqual(CollectionsMarshal.AsSpan(CurrentStarts)) &&
                ComponentResourceGroups.AccessesMatch(CollectionsMarshal.AsSpan(Accesses), CollectionsMarshal.AsSpan(CurrentAccesses))) return;
            Starts.Clear(); Starts.AddRange(CurrentStarts);
            Accesses.Clear(); Accesses.AddRange(CurrentAccesses);
            var groups = new ComponentResourceGroups(count);
            var buckets = new Dictionary<ComponentResourceIdentity, List<ComponentResourceRange>>();
            for (int row = 0; row < count; row++)
                for (int index = Starts[row]; index < Starts[row + 1]; index++)
                    ComponentResourceGroups.AddAccess(Accesses[index], row, buckets);
            foreach (List<ComponentResourceRange> ranges in buckets.Values) groups.JoinRanges(ranges);
            Groups = groups.Build();
        }
    }

    private readonly struct ExecuteCompatibleGroups<TJob>(EntityQueryData[][] queries, int[][] groups, TJob job,
        int queryStart, int workStart) : IJobParallelFor where TJob : struct, IEntityQueryJob
    {
        public void Execute(int index)
        {
            foreach (int row in groups[index])
            {
                TJob local = job;
                local.Execute(workStart + row, new(queries[queryStart + row]));
            }
        }
    }
}
