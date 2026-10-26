using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Queries;
using SomeEngine.Diagnostics;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

internal sealed class ComponentResourceQueryCache
{
    internal ComponentResourceQueryData? Data;
}

internal sealed class ComponentResourceQueryData(
    StableQueryPacketSet packets,
    GeneratedQueryAccessDescriptor descriptor,
    JobEntityScheduleOptions options,
    bool parallel,
    JobResourceAccess[] captureAccesses)
{
    internal StableQueryPacketSet Packets { get; } = packets;
    internal GeneratedQueryAccessDescriptor Descriptor { get; } = descriptor;
    internal JobEntityScheduleOptions Options { get; } = options;
    internal bool Parallel { get; } = parallel;
    internal JobResourceAccess[] CaptureAccesses { get; } = captureAccesses;
    internal ComponentResourceRow[][] PacketRows { get; } = new ComponentResourceRow[packets.Packets.Length][];
    internal ComponentResourceRow[] Rows = [];
    internal ComponentResourceRows Groups = ComponentResourceRows.Empty;
    internal JobResourceAccess[] Accesses = [];
    internal ComponentResourceColumn[] Columns = [];
    internal long[] EnabledWrites = [];
}

internal readonly record struct ComponentResourceColumn(Chunk Chunk, int Column, long Writes);

internal sealed record ComponentResourceRow(
    QueryPacket Packet,
    int Row,
    ComponentResourceUse[] Uses,
    object[] References,
    long[] ResourceVersions);

public static partial class JobEntityRuntime
{
    private static JobHandle ScheduleComponentResources<TJob, TAdapter>(
        World world,
        in TJob job,
        TAdapter adapter,
        GeneratedQueryAccessDescriptor descriptor,
        ReadOnlySpan<JobResourceAccess> externalAccesses,
        JobEntityScheduleOptions options,
        JobHandle dependency,
        bool parallel)
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        JobResourceAccess[] captureAccesses = BuildCaptureAccesses(world, externalAccesses);
        var reads = new JobResourceAccess[captureAccesses.Length + descriptor.AccessCount];
        captureAccesses.CopyTo(reads, 0);
        for (int i = 0; i < descriptor.AccessCount; i++)
            reads[captureAccesses.Length + i] = WorldStorageJobResources.Read(world, Key(descriptor.GetAccess(i)));
        var request = new ComponentResourceRequest<TJob, TAdapter>(
            world, job, adapter, descriptor, captureAccesses, reads, options, parallel);
        Scheduler scheduler = WorldStorageJobResources.RequireJobs(world).Scheduler;
        JobHandle handle = scheduler.Schedule(new CollectComponentResources<TJob, TAdapter>(request),
            reads, options.JobOptions, dependency);
        return KeepJobData(scheduler, handle, in job);
    }

    private sealed class ComponentResourceRequest<TJob, TAdapter>(
        World world,
        TJob job,
        TAdapter adapter,
        GeneratedQueryAccessDescriptor descriptor,
        JobResourceAccess[] captureAccesses,
        JobResourceAccess[] readAccesses,
        JobEntityScheduleOptions options,
        bool parallel)
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        internal World World { get; } = world;
        internal TJob Job { get; } = job;
        internal TAdapter Adapter { get; } = adapter;
        internal GeneratedQueryAccessDescriptor Descriptor { get; } = descriptor;
        internal JobResourceAccess[] CaptureAccesses { get; } = captureAccesses;
        internal JobResourceAccess[] ReadAccesses { get; } = readAccesses;
        internal JobEntityScheduleOptions Options { get; } = options;
        internal bool Parallel { get; } = parallel;
        internal Scheduler Scheduler => WorldStorageJobResources.RequireJobs(World).Scheduler;

        internal void Retry() => JobSystem.Schedule(
            new CollectComponentResources<TJob, TAdapter>(this), ReadAccesses, Options.JobOptions);

        internal void Execute(ComponentResourceQueryData data)
        {
            JobHandle handle = JobSystem.Schedule(
                new ExecuteComponentResources<TJob, TAdapter>(this, data), data.Accesses, Options.JobOptions);
            Scheduler.OnCompleted(handle, static (_, references) => GC.KeepAlive(references), data);
        }
    }

    private readonly struct CollectComponentResources<TJob, TAdapter>(
        ComponentResourceRequest<TJob, TAdapter> request) : IJob
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(ComponentResourceTrace<TJob>.Collect);
            ComponentResourceQueryCache cache = request.Descriptor.ResourceCache(request.World);
            ComponentResourceQueryData? previous = Volatile.Read(ref cache.Data);
            if (previous is not null &&
                (!request.Descriptor.HasVersionFilters ||
                    previous.Options.LastSystemVersion == request.Options.LastSystemVersion) &&
                previous.Parallel == request.Parallel &&
                previous.Options.RowsPerPacket == request.Options.RowsPerPacket &&
                SameExternalAccesses(previous.CaptureAccesses, request.CaptureAccesses) &&
                ResourceInputsMatch(previous))
            {
                request.Execute(previous);
                return;
            }

            QueryHandle query = request.Descriptor.Resolve(request.World);
            StableQueryPacketSet packets = CapturePackets(request.World, query,
                request.Options.RowsPerPacket, request.Options.LastSystemVersion);
            var data = new ComponentResourceQueryData(packets, request.Descriptor, request.Options,
                request.Parallel, request.CaptureAccesses);
            var columns = new List<ComponentResourceColumn>();
            data.EnabledWrites = new long[packets.Packets.Length];
            for (int p = 0; p < packets.Packets.Length; p++)
            {
                QueryPacket packet = packets.Packets[p];
                data.EnabledWrites[p] = packet.Chunk.EnabledWrites;
                for (int a = 0; a < request.Descriptor.AccessCount; a++)
                {
                    GeneratedQueryAccess access = request.Descriptor.GetAccess(a);
                    if (access.Resources is null && access.Filters == QueryTermFilter.None)
                        continue;
                    if (packet.Match.Archetype.TryColumn(access.ComponentId, out int column))
                        columns.Add(new ComponentResourceColumn(packet.Chunk, column,
                            packet.Chunk.ColumnWrites(column)));
                }
            }
            data.Columns = columns.ToArray();
            if (packets.Packets.Length == 0)
            {
                data.Accesses = request.CaptureAccesses;
                Volatile.Write(ref cache.Data, data);
                return;
            }
            var discovery = new ComponentResourceDiscovery<TJob, TAdapter>(request, data);
            request.Scheduler.ScheduleParallelInCurrentScope(
                new CollectPacketResources<TJob, TAdapter>(discovery), packets.Packets.Length, request.Options.JobOptions);
        }
    }

    private sealed class ComponentResourceDiscovery<TJob, TAdapter>(
        ComponentResourceRequest<TJob, TAdapter> request,
        ComponentResourceQueryData data)
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        internal ComponentResourceRequest<TJob, TAdapter> Request { get; } = request;
        internal ComponentResourceQueryData Data { get; } = data;
        internal int Remaining = data.Packets.Packets.Length;
        internal int Failed;
    }

    private readonly struct CollectPacketResources<TJob, TAdapter>(
        ComponentResourceDiscovery<TJob, TAdapter> discovery) : IJobParallelFor
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute(int index)
        {
            using var trace = EngineTrace.Begin(ComponentResourceTrace<TJob>.ReadPacket);
            ComponentResourceQueryData data = discovery.Data;
            QueryPacket packet = data.Packets.Packets[index];
            World world = discovery.Request.World;
            var rows = new List<ComponentResourceRow>();
            world.ActiveStructureRoot.Iteration.BeginQueryBorrow();
            try
            {
                if (packet.Match.ChunkColumns.Length != 0 &&
                    !packet.Match.MatchesChunkOnly(packet.Chunk, data.Options.LastSystemVersion))
                {
                    data.PacketRows[index] = [];
                    return;
                }
                int end = packet.Range.RowEnd;
                for (int r = packet.Range.RowStart; r < end; r++)
                {
                    if (!MatchesResourceRow(data, in packet, r))
                        continue;
                    var uses = new List<ComponentResourceUse>();
                    var references = new List<object>();
                    var versions = new List<long>();
                    var builder = new JobResourceAccessBuilder(discovery.Request.Scheduler, uses, references);
                    var row = new JobEntityRow(world, packet.Match, packet.Chunk, data.Descriptor, r, 0);
                    for (int a = 0; a < data.Descriptor.AccessCount; a++)
                    {
                        if (data.Descriptor.GetAccess(a).Resources is not { } resources)
                            continue;
                        versions.Add(resources.GetVersion(ref row));
                        resources.Collect(ref row, ref builder);
                    }
                    rows.Add(new ComponentResourceRow(packet, r, uses.ToArray(), references.ToArray(), versions.ToArray()));
                }
                data.PacketRows[index] = rows.ToArray();
            }
            catch
            {
                Volatile.Write(ref discovery.Failed, 1);
                throw;
            }
            finally
            {
                world.ActiveStructureRoot.Iteration.EndQueryBorrow();
                if (Interlocked.Decrement(ref discovery.Remaining) == 0 && Volatile.Read(ref discovery.Failed) == 0)
                    JobSystem.Schedule(new CombineComponentResources<TJob, TAdapter>(discovery));
            }
        }
    }

    private readonly struct CombineComponentResources<TJob, TAdapter>(
        ComponentResourceDiscovery<TJob, TAdapter> discovery) : IJob
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(ComponentResourceTrace<TJob>.Combine);
            ComponentResourceQueryData data = discovery.Data;
            ComponentResourceRequest<TJob, TAdapter> request = discovery.Request;
            data.Rows = data.PacketRows.SelectMany(static rows => rows).ToArray();
            var accesses = new List<JobResourceAccess>(request.Parallel
                ? BuildPacketAccesses(request.World, data.Descriptor, data.Packets, request.CaptureAccesses)
                : BuildWholeAccesses(request.World, data.Descriptor, request.CaptureAccesses));
            var buckets = new Dictionary<ComponentResourceIdentity, List<ComponentResourceRange>>();
            for (int r = 0; r < data.Rows.Length; r++)
            {
                foreach (ComponentResourceUse use in data.Rows[r].Uses)
                {
                    accesses.Add(use.Access);
                    AddRange(use.Access, r, buckets);
                }
                for (int a = 1; a < request.CaptureAccesses.Length; a++)
                    AddRange(request.CaptureAccesses[a], r, buckets);
            }
            data.Accesses = accesses.ToArray();
            var grouping = new ComponentResourceGrouping<TJob, TAdapter>(request, data, buckets.Values.ToArray());
            if (grouping.Buckets.Length == 0)
            {
                grouping.Finish();
                return;
            }
            request.Scheduler.ScheduleParallelInCurrentScope(
                new GroupComponentResources<TJob, TAdapter>(grouping), grouping.Buckets.Length, request.Options.JobOptions);
        }

        private static void AddRange(JobResourceAccess access, int row,
            Dictionary<ComponentResourceIdentity, List<ComponentResourceRange>> buckets) =>
            ComponentResourceGroups.AddAccess(access, row, buckets);
    }
    private sealed class ComponentResourceGrouping<TJob, TAdapter>(
        ComponentResourceRequest<TJob, TAdapter> request,
        ComponentResourceQueryData data,
        List<ComponentResourceRange>[] buckets)
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        internal List<ComponentResourceRange>[] Buckets { get; } = buckets;
        internal readonly ComponentResourceGroups Groups = new(data.Rows.Length);
        internal int Remaining = buckets.Length;
        internal int Failed;

        internal void Finish()
        {
            data.Groups = Groups.Build();
            Volatile.Write(ref request.Descriptor.ResourceCache(request.World).Data, data);
            request.Execute(data);
        }
    }

    private readonly struct GroupComponentResources<TJob, TAdapter>(
        ComponentResourceGrouping<TJob, TAdapter> grouping) : IJobParallelFor
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute(int index)
        {
            using var trace = EngineTrace.Begin(ComponentResourceTrace<TJob>.Group);
            try
            {
                grouping.Groups.JoinRanges(grouping.Buckets[index]);
            }
            catch
            {
                Volatile.Write(ref grouping.Failed, 1);
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref grouping.Remaining) == 0 && Volatile.Read(ref grouping.Failed) == 0)
                    grouping.Finish();
            }
        }
    }

    private static bool SameExternalAccesses(JobResourceAccess[] accesses, JobResourceAccess[] captureAccesses) =>
        ComponentResourceGroups.AccessesMatch(accesses, captureAccesses);
    private static bool ResourceInputsMatch(ComponentResourceQueryData data)
    {
        World world = data.Packets.World;
        if (world.PublishedStructureEpoch != data.Packets.Proof.StructureEpoch ||
            world.PublishedTopologyRevision != data.Packets.Proof.TopologyRevision)
            return false;
        for (int i = 0; i < data.Packets.Packets.Length; i++)
            if (data.Packets.Packets[i].Chunk.EnabledWrites != data.EnabledWrites[i])
                return false;
        foreach (ComponentResourceColumn column in data.Columns)
            if (column.Chunk.ColumnWrites(column.Column) != column.Writes)
                return false;
        if (data.Descriptor.HasSparseAccess)
        {
            for (int p = 0; p < data.Packets.Packets.Length; p++)
            {
                QueryPacket packet = data.Packets.Packets[p];
                ComponentResourceRow[] rows = data.PacketRows[p];
                int selected = 0;
                for (int r = packet.Range.RowStart; r < packet.Range.RowEnd; r++)
                {
                    if (!MatchesResourceRow(data, in packet, r))
                        continue;
                    if (selected == rows.Length || rows[selected++].Row != r)
                        return false;
                }
                if (selected != rows.Length)
                    return false;
            }
        }
        foreach (ComponentResourceRow source in data.Rows)
        {
            var row = new JobEntityRow(world, source.Packet.Match, source.Packet.Chunk,
                data.Descriptor, source.Row, 0);
            int version = 0;
            for (int a = 0; a < data.Descriptor.AccessCount; a++)
                if (data.Descriptor.GetAccess(a).Resources is { } resources &&
                    resources.GetVersion(ref row) != source.ResourceVersions[version++])
                    return false;
        }
        return true;
    }

    private static bool MatchesResourceRow(ComponentResourceQueryData data, in QueryPacket packet, int row)
    {
        if (!packet.Match.MatchesChunkOnly(packet.Chunk, data.Options.LastSystemVersion) ||
            !packet.Match.MatchesRow(packet.Chunk, row, data.Options.LastSystemVersion))
            return false;
        for (int a = 0; a < data.Descriptor.AccessCount; a++)
        {
            GeneratedQueryAccess access = data.Descriptor.GetAccess(a);
            if (access.Storage == GeneratedQueryStorage.Sparse && access.HasDirectAccess &&
                !data.Packets.World.Sparse.HasValue(access.ComponentId, packet.Chunk.Entities[row]))
                return false;
        }
        return true;
    }

    private readonly struct ExecuteComponentResources<TJob, TAdapter>(
        ComponentResourceRequest<TJob, TAdapter> request,
        ComponentResourceQueryData data) : IJob
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute()
        {
            bool matches;
            using (EngineTrace.Begin(ComponentResourceTrace<TJob>.CheckInputs))
                matches = ResourceInputsMatch(data);
            if (!matches)
            {
                request.Retry();
                return;
            }
            if (data.Rows.Length == 0)
                return;
            if (!request.Parallel)
            {
                new SerialJob<TJob, TAdapter>(request.World, request.Descriptor.Resolve(request.World),
                    request.Job, request.Adapter, request.Descriptor, request.Options.LastSystemVersion).Execute();
                return;
            }
            uint writeVersion = request.Descriptor.HasWorldWrites ? request.World.AcquireSystemVersion() : 0;
            int batchSize = request.Options.RowsPerPacket == 0
                ? JobScheduleOptions.AutomaticBatchSize : request.Options.RowsPerPacket;
            using var trace = EngineTrace.Begin(ComponentResourceTrace<TJob>.Execute);
            request.Scheduler.ExecuteParallel(
                new ExecuteResourceGroups<TJob, TAdapter>(request, data, writeVersion),
                data.Groups.Count, batchSize, request.Options.JobOptions);
        }
    }

    private readonly struct ExecuteResourceGroups<TJob, TAdapter>(
        ComponentResourceRequest<TJob, TAdapter> request,
        ComponentResourceQueryData data,
        uint writeVersion) : IJobParallelFor
        where TJob : struct, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        public void Execute(int index)
        {
            TJob job = request.Job;
            TAdapter adapter = request.Adapter;
            request.World.ActiveStructureRoot.Iteration.BeginQueryBorrow();
            try
            {
                foreach (int rowIndex in data.Groups[index])
                {
                    ComponentResourceRow source = data.Rows[rowIndex];
                    var row = new JobEntityRow(request.World, source.Packet.Match, source.Packet.Chunk,
                        request.Descriptor, source.Row, writeVersion);
                    adapter.Execute(ref job, ref row);
                }
            }
            finally
            {
                request.World.ActiveStructureRoot.Iteration.EndQueryBorrow();
            }
        }
    }
}

internal static class ComponentResourceTrace<TJob>
{
    internal static readonly TraceSite Collect = new("ecs", $"Resources.Collect/{typeof(TJob)}");
    internal static readonly TraceSite ReadPacket = new("ecs", $"Resources.ReadPacket/{typeof(TJob)}");
    internal static readonly TraceSite Combine = new("ecs", $"Resources.Combine/{typeof(TJob)}");
    internal static readonly TraceSite Group = new("ecs", $"Resources.Group/{typeof(TJob)}");
    internal static readonly TraceSite CheckInputs = new("ecs", $"Resources.CheckInputs/{typeof(TJob)}");
    internal static readonly TraceSite Execute = new("ecs", $"Resources.Execute/{typeof(TJob)}");
}
