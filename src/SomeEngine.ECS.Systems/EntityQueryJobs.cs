using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public static partial class EntityQueryJobs
{
    private static readonly ConditionalWeakTable<World, Workspaces> s_workspaces = new();

    /// <summary>Schedules actual component ranges and keeps all row borrows within each scalar work callback.</summary>
    public static JobHandle Schedule<TWork, TJob>(World world, in TWork work, in TJob job,
        ReadOnlySpan<JobResourceAccess> accesses = default, QueryReadResources? resources = null, JobHandle dependency = default)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        Workspaces available = s_workspaces.GetValue(world, static _ => new());
        Workspace workspace;
        lock (available.Gate) workspace = available.Items.Count == 0 ? new() : available.Items.Pop();
        workspace.Reset();
        var request = new WorkRequest<TWork, TJob>(world, work, job, accesses.ToArray(), resources, workspace);
        JobHandle handle = scheduler.Schedule(new Capture<TWork, TJob>(request), request.CaptureAccesses, dependency);
        scheduler.OnCompleted(handle, static (_, state) =>
        {
            var done = ((Workspaces Available, Workspace Workspace))state!;
            lock (done.Available.Gate) done.Available.Items.Push(done.Workspace);
        }, (available, workspace));
        return handle;
    }

    private sealed class Workspaces
    {
        internal readonly Lock Gate = new();
        internal readonly Stack<Workspace> Items = [];
    }

    private sealed class Workspace
    {
        internal readonly List<Batch> Batches = [];
        internal readonly List<JobResourceAccess> GroupAccesses = [];
        internal EntityQueryData[][] GroupQueries = [];
        internal EntityQueryData[] AllQueries = [];
        internal readonly GroupRanges ParallelRanges = new();
        private readonly Lock _versionGate = new();
        private bool _hasVersion;
        private uint _version;
        internal void Reset() => _hasVersion = false;
        internal uint Version(World world)
        {
            if (Volatile.Read(ref _hasVersion)) return _version;
            lock (_versionGate)
            {
                if (!_hasVersion) { _version = world.AcquireAdmittedSystemVersion(); Volatile.Write(ref _hasVersion, true); }
                return _version;
            }
        }
        internal Batch Get(int index, int queryCount)
        {
            while (Batches.Count <= index) Batches.Add(new());
            Batch batch = Batches[index];
            if (batch.Queries.Length != queryCount)
                batch.Queries = Enumerable.Range(0, queryCount).Select(static _ => new EntityQueryData()).ToArray();
            batch.Accesses.Clear();
            return batch;
        }
    }

    private static Batch CaptureQueries<TWork>(World world, in TWork work, int index, Workspace workspace, int batchIndex,
        QueryReadResources? resources) where TWork : struct, IEntityQueryWork
    {
        Batch batch = workspace.Get(batchIndex, work.QueryCount);
        batch.Topology = world.PublishedTopologyRevision;
        batch.Structure = world.PublishedStructureEpoch;
        for (int query = 0; query < work.QueryCount; query++)
        {
            EntityQueryData data = batch.Queries[query];
            data.Set(world, work.GetQuery(index, query), work.GetEntities(index, query));
            batch.AddRanges(world, data, resources);
        }
        return batch;
    }

    private static uint SetVersion(World world, Workspace workspace, ReadOnlySpan<EntityQueryData> queries)
    {
        bool writes = false;
        foreach (EntityQueryData query in queries) writes |= query.Definition.CanWrite;
        uint version = writes ? workspace.Version(world) : 0;
        SetVersion(world, version, queries);
        return version;
    }

    private static void SetVersion(World world, uint version, ReadOnlySpan<EntityQueryData> queries)
    {
        foreach (EntityQueryData query in queries) query.Data = new(world, query.Data.State, 0, version);
    }

    private sealed class Batch
    {
        internal EntityQueryData[] Queries = [];
        internal long Topology, Structure;
        internal readonly List<JobResourceAccess> Accesses = [];
        internal readonly List<long> Addresses = [];
        internal readonly HashSet<long> ReadChunks = [];

        internal void AddRanges(World world, EntityQueryData query, QueryReadResources? resources)
        {
            if (query.Definition.HasRelationshipWrite)
                throw new InvalidOperationException("Entity query work uses ordinary component writes; relationship endpoints use structural commands.");
            foreach (WorldJobStorageAccess storage in query.Definition.JobStorageAccesses.Span)
            {
                QueryJobAccess.RequireStorage(storage, resources);
                Addresses.Clear();
                ReadChunks.Clear();
                bool chunkChanged = false;
                foreach (QueryDataAccess access in query.Definition.DataAccesses)
                    if (access.Storage == storage.Kind && access.ComponentId == storage.ComponentId)
                        chunkChanged = (access.Filters & QueryTermFilter.ChunkChanged) != 0;
                var key = new WorldStorageResourceKey(storage.Kind, storage.ComponentId);
                foreach (Entity entity in query.Entities.AsSpan(0, query.Count))
                {
                    if (!world.ActiveStructureRoot.Entities.Alive(entity)) continue;
                    EntityRecord record = world.ActiveStructureRoot.Entities.ReadRow(entity);
                    if (storage.Kind == WorldStorageKind.Sparse) Addresses.Add(entity.Index);
                    else if (record.Archetype!.HasComponent(storage.ComponentId))
                    {
                        Chunk chunk = record.Chunk!;
                        var range = new StableQueryPacketRange(chunk.PersistentIdentity, record.RowInChunk, 1, chunk.Count);
                        Addresses.Add(StableQueryPacketAddress.Address(in range));
                        if (chunkChanged && ReadChunks.Add(chunk.PersistentIdentity))
                        {
                            var full = new StableQueryPacketRange(chunk.PersistentIdentity, 0, chunk.Count, chunk.Count);
                            Accesses.Add(WorldStorageJobResources.Read(world, key, StableQueryPacketAddress.Address(in full), chunk.Count));
                        }
                    }
                }
                Addresses.Sort();
                int index = 0;
                while (index < Addresses.Count)
                {
                    long first = Addresses[index], last = first;
                    while (++index < Addresses.Count && Addresses[index] <= last + 1) last = Math.Max(last, Addresses[index]);
                    Accesses.Add(storage.Access == WorldStorageAccess.Write
                        ? WorldStorageJobResources.Write(world, key, first, last - first + 1)
                        : WorldStorageJobResources.Read(world, key, first, last - first + 1));
                }
            }
        }
    }

    private sealed class WorkRequest<TWork, TJob>(World world, TWork work, TJob job, JobResourceAccess[] external,
        QueryReadResources? resources, Workspace workspace)
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        internal readonly JobResourceAccess[] CaptureAccesses = [RelationshipJobAccess.TopologyRead(world), .. external];

        internal void Capture()
        {
            for (int index = 0; index < work.Count; index++) Capture(index);
        }

        internal void Capture(int index)
        {
            Batch batch = CaptureQueries(world, in work, index, workspace, index, resources);
            batch.Accesses.AddRange(CaptureAccesses);
            var run = new RunBatch<TWork, TJob>(this, index);
            if (resources is null)
                JobSystem.Schedule(run, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch.Accesses));
            else
                resources.ScheduleEntityQueries(world, batch.Queries, in run, new CaptureBatch<TWork, TJob>(this, index),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch.Accesses), batch.Topology, batch.Structure);
        }

        internal void Execute(int index)
        {
            Batch batch = workspace.Batches[index];
            if (world.PublishedTopologyRevision != batch.Topology || world.PublishedStructureEpoch != batch.Structure)
            {
                JobSystem.Schedule(new CaptureBatch<TWork, TJob>(this, index), CaptureAccesses);
                return;
            }
            SetVersion(world, workspace, batch.Queries);
            var iteration = world.ActiveStructureRoot.Iteration;
            iteration.BeginQueryBorrow();
            try
            {
                TJob local = job;
                local.Execute(index, new EntityQueryViews(batch.Queries));
            }
            finally { iteration.EndQueryBorrow(); }
        }
    }

    private readonly struct Capture<TWork, TJob>(WorkRequest<TWork, TJob> request) : IJob
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        public void Execute() => request.Capture();
    }
    private readonly struct RunBatch<TWork, TJob>(WorkRequest<TWork, TJob> request, int index) : IJob
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        public void Execute() => request.Execute(index);
    }
    private readonly struct CaptureBatch<TWork, TJob>(WorkRequest<TWork, TJob> request, int index) : IJob
        where TWork : struct, IEntityQueryWork where TJob : struct, IEntityQueryJob
    {
        public void Execute() => request.Capture(index);
    }
}
