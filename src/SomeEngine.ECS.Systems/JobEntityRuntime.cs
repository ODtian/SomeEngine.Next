using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Sparse;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Row capability created only inside a generated runtime callback. It is a ref-struct, has no
/// public constructor, and resolves refs/buffers/sparse values only from the admitted packet.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct JobEntityRow
{
    private readonly World _world;
    private readonly QueryArchetypeMatch _match;
    private readonly Chunk _chunk;
    private readonly GeneratedQueryAccessDescriptor _descriptor;
    private readonly int _row;
    private readonly uint _writeVersion;

    internal JobEntityRow(
        World world,
        QueryArchetypeMatch match,
        Chunk chunk,
        GeneratedQueryAccessDescriptor descriptor,
        int row,
        uint writeVersion)
    {
        _world = world;
        _match = match;
        _chunk = chunk;
        _descriptor = descriptor;
        _row = row;
        _writeVersion = writeVersion;
    }

    public Entity Entity => _chunk.Entities[_row];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>()
        where T : struct, IComponent
    {
        int column = Require<T>(read: true, write: false);
        return ref _chunk.GetComponentReadOnlyRef<T>(column, _row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T ReadWrite<T>()
        where T : struct, IComponent
    {
        int column = Require<T>(read: true, write: true);
        return ref _world.Components.WriteRef<T>(
            Entity,
            _chunk,
            _row,
            column,
            _writeVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferView<T> ReadBuffer<T>()
        where T : struct, IBufferElement
    {
        RequireBufferCapability<T>(write: false);
        return _world.Buffers.BorrowRead<T>(Entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DynamicBuffer<T> ReadWriteBuffer<T>()
        where T : struct, IBufferElement
    {
        RequireBufferCapability<T>(write: true);
        return _world.Buffers.BorrowWrite<T>(Entity, _writeVersion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSparse<T>()
        where T : struct, ISparseComponent
    {
        RequireSparseCapability<T>(write: false);
        return _world.Sparse.HasValue<T>(Entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T ReadSparse<T>()
        where T : struct, ISparseComponent
    {
        RequireSparseCapability<T>(write: false);
        if (!_world.Sparse.TrySet<T>(out SparseSet<T>? set) || !set.Has(Entity))
        {
            throw new InvalidOperationException(
                $"Entity {Entity} does not have sparse component {typeof(T).Name}.");
        }
        return ref set.ReadRef(Entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T ReadWriteSparse<T>()
        where T : struct, ISparseComponent
    {
        RequireSparseCapability<T>(write: true);
        if (!_world.Sparse.TrySet<T>(out SparseSet<T>? set) || !set.Has(Entity))
        {
            throw new InvalidOperationException(
                $"Entity {Entity} does not have sparse component {typeof(T).Name}.");
        }
        return ref set.Get(Entity);
    }

    private int Require<T>(bool read, bool write)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        _descriptor.RequireDirectAccess(
            GeneratedQueryStorage.Table,
            componentId,
            write);
        if (!_match.TryGetAccess(componentId, out QueryColumnAccess access) ||
            (read && !access.Access.CanRead()) ||
            (write && !access.Access.CanWrite()))
        {
            throw new InvalidOperationException(
                $"Generated query did not declare the required access to {typeof(T).Name}.");
        }
        return access.ColumnIndex;
    }

    private void RequireBufferCapability<T>(bool write)
        where T : struct, IBufferElement
    {
        _descriptor.RequireDirectAccess(
            GeneratedQueryStorage.Buffer,
            BufferComponents.Header<T>(),
            write);
    }

    private void RequireSparseCapability<T>(bool write)
        where T : struct, ISparseComponent
    {
        _descriptor.RequireDirectAccess(
            GeneratedQueryStorage.Sparse,
            ComponentMetadata<T>.Id,
            write);
    }
}

/// <summary>Narrow runtime entry used by source-generated IJobEntity extensions.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JobEntityRuntime
{
    public static JobHandle Schedule<TJob, TAdapter>(
        World world,
        in TJob job,
        TAdapter adapter,
        GeneratedQueryAccessDescriptor descriptor,
        JobEntityScheduleOptions options = default,
        JobHandle dependency = default)
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(descriptor);
        WorldStorageJobResources.Bind(world);
        descriptor = descriptor.WithFilter(options.Filter);
        var capture = new SerialQueryCaptureJob<TJob, TAdapter>(
            world,
            job,
            adapter,
            descriptor,
            options);
        return JobSystem.Schedule(
            capture,
            RelationshipJobAccess.TopologyRead(world),
            options.JobOptions,
            dependency);
    }

    public static JobHandle ScheduleParallel<TJob, TAdapter>(
        World world,
        in TJob job,
        TAdapter adapter,
        GeneratedQueryAccessDescriptor descriptor,
        JobEntityScheduleOptions options = default,
        JobHandle dependency = default)
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(descriptor);
        WorldStorageJobResources.Bind(world);
        descriptor = descriptor.WithFilter(options.Filter);
        if (!descriptor.SupportsParallel)
        {
            throw new InvalidOperationException(
                "ScheduleParallel requires unmanaged direct accesses and cannot write canonical relationship components.");
        }

        // Delayed resource activation keeps the topology-read reservation out of the conflict
        // frontier until the full semantic dependency, including attached descendants, completes.
        // The capture then resolves stable packets and attaches their resource-bearing dispatch to
        // the returned scope before releasing its own topology-read work grant.
        var capture = new PacketCaptureJob<TJob, TAdapter>(
            world,
            job,
            adapter,
            descriptor,
            options);
        return JobSystem.Schedule(
            capture,
            RelationshipJobAccess.TopologyRead(world),
            options.JobOptions,
            dependency);
    }

    public static void Execute<TJob, TAdapter>(
        World world,
        in TJob job,
        TAdapter adapter,
        GeneratedQueryAccessDescriptor descriptor,
        JobEntityScheduleOptions options = default,
        JobHandle dependency = default)
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        JobHandle handle = Schedule(
            world,
            in job,
            adapter,
            descriptor,
            options,
            dependency);
        handle.Complete();
    }

    /// <summary>Returns only immutable packet proof metadata; no packet storage capability escapes.</summary>
    public static StableQueryPartitionProof DescribePartition(
        World world,
        GeneratedQueryAccessDescriptor descriptor,
        int rowsPerPacket = 0,
        uint lastSystemVersion = 0,
        QueryDefinition? filter = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(descriptor);
        WorldStorageJobResources.Bind(world);
        descriptor = descriptor.WithFilter(filter);
        if (!descriptor.SupportsParallel)
        {
            throw new InvalidOperationException(
                "A parallel partition proof cannot include relationship writes, external aliases, or a chunk-wide filter that aliases a direct write.");
        }
        _ = RelationshipJobAccess.TopologyRead(world);
        QueryHandle query = descriptor.Resolve(world);
        using WorldJobAdmissionScope admission = world.EnterJobQuery(query, out bool relationshipWrite);
        if (relationshipWrite)
            throw new InvalidOperationException("A parallel partition cannot own relationship writes.");
        return CapturePackets(world, query, rowsPerPacket, lastSystemVersion).Proof;
    }

    private static StableQueryPacketSet CapturePackets(
        World world,
        QueryHandle query,
        int rowsPerPacket,
        uint lastSystemVersion)
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));

        QueryState state = world.ActiveStructureRoot.Queries.Get(query).State;
        var packets = new List<QueryPacket>();
        var ranges = new List<StableQueryPacketRange>();
        ReadOnlySpan<QueryArchetypeMatch> matches = state.Matches;
        for (int matchIndex = 0; matchIndex < matches.Length; matchIndex++)
        {
            QueryArchetypeMatch match = matches[matchIndex];
            ReadOnlySpan<Chunk> chunks = match.Archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                Chunk chunk = chunks[chunkIndex];
                int count = chunk.Count;
                if (count <= 0)
                    continue;

                long persistentIdentity = chunk.PersistentIdentity;
                if (persistentIdentity <= 0 ||
                    persistentIdentity > long.MaxValue / StableQueryPacketAddress.RowsPerChunkStride)
                {
                    throw new InvalidOperationException("Stable query chunk identity space is exhausted.");
                }
                int packetRows = rowsPerPacket == 0 ? count : rowsPerPacket;
                for (int start = 0; start < count; start += packetRows)
                {
                    int length = Math.Min(packetRows, count - start);
                    var range = new StableQueryPacketRange(
                        persistentIdentity,
                        start,
                        length,
                        count);
                    ranges.Add(range);
                    packets.Add(new QueryPacket(
                        match,
                        chunk,
                        range));
                }
            }
        }

        StableQueryPacketRange[] rangeArray = ranges.ToArray();
        long structureEpoch = world.PublishedStructureEpoch;
        long topologyRevision = world.PublishedTopologyRevision;
        return new StableQueryPacketSet(
            world,
            packets.ToArray(),
            new StableQueryPartitionProof(rangeArray, structureEpoch, topologyRevision),
            lastSystemVersion);
    }

    private static JobResourceAccess[] BuildWholeAccesses(
        World world,
        GeneratedQueryAccessDescriptor descriptor)
    {
        var accesses = new JobResourceAccess[descriptor.AccessCount + 1];
        accesses[0] = descriptor.HasRelationshipWrite
            ? RelationshipJobAccess.TopologyWrite(world)
            : RelationshipJobAccess.TopologyRead(world);
        for (int i = 0; i < descriptor.AccessCount; i++)
            accesses[i + 1] = WholeAccess(world, descriptor.GetAccess(i));
        return accesses;
    }

    private static JobResourceAccess[] BuildPacketAccesses(
        World world,
        GeneratedQueryAccessDescriptor descriptor,
        StableQueryPacketSet packets)
    {
        var accesses = new List<JobResourceAccess>
        {
            RelationshipJobAccess.TopologyRead(world),
        };

        for (int accessIndex = 0; accessIndex < descriptor.AccessCount; accessIndex++)
        {
            GeneratedQueryAccess access = descriptor.GetAccess(accessIndex);
            if (access.Storage == GeneratedQueryStorage.Sparse)
            {
                if (access.HasDirectAccess)
                    AddSparseRanges(world, access, packets.Packets, accesses);
                continue;
            }

            // Stable packet proof covers every captured chunk from row zero through its tail.
            // The entire parallel dispatch is one logical resource owner, so retaining one range
            // per packet only makes the frontier scan O(packet count) without admitting any extra
            // external overlap. Collapse each logical family to one exact range per chunk; the
            // packet proof and row capability still restrict individual work items.
            long previousChunk = 0;
            for (int packetIndex = 0; packetIndex < packets.Packets.Length; packetIndex++)
            {
                QueryPacket packet = packets.Packets[packetIndex];
                StableQueryPacketRange range = packet.Range;
                if (range.PersistentChunkId == previousChunk)
                    continue;
                previousChunk = range.PersistentChunkId;

                var chunkRange = new StableQueryPacketRange(
                    range.PersistentChunkId,
                    rowStart: 0,
                    packet.Chunk.Count,
                    packet.Chunk.Count);
                long chunkStart = StableQueryPacketAddress.Address(in chunkRange);
                if (access.HasDirectAccess)
                    accesses.Add(RangeAccess(world, access, chunkStart, chunkRange.RowCount));
                else if (access.Filters != QueryTermFilter.None)
                    accesses.Add(ReadRangeAccess(world, access, chunkStart, chunkRange.RowCount));
            }
        }

        return accesses.ToArray();
    }

    private static void AddSparseRanges(
        World world,
        GeneratedQueryAccess access,
        ReadOnlySpan<QueryPacket> packets,
        List<JobResourceAccess> destination)
    {
        int entityCount = 0;
        for (int i = 0; i < packets.Length; i++)
            entityCount += packets[i].Range.RowCount;
        if (entityCount == 0)
            return;

        int[] rented = ArrayPool<int>.Shared.Rent(entityCount);
        try
        {
            int written = 0;
            for (int i = 0; i < packets.Length; i++)
            {
                QueryPacket packet = packets[i];
                int end = packet.Range.RowStart + packet.Range.RowCount;
                for (int row = packet.Range.RowStart; row < end; row++)
                    rented[written++] = packet.Chunk.Entities[row].Index;
            }
            Array.Sort(rented, 0, written);
            int start = rented[0];
            int previous = start;
            for (int i = 1; i <= written; i++)
            {
                if (i < written && rented[i] == previous)
                {
                    throw new InvalidOperationException(
                        "Stable query packets contain the same Entity more than once.");
                }
                if (i < written && rented[i] == previous + 1)
                {
                    previous = rented[i];
                    continue;
                }
                destination.Add(RangeAccess(
                    world,
                    access,
                    start,
                    checked(previous - start + 1)));
                if (i < written)
                    start = previous = rented[i];
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static JobResourceAccess WholeAccess(World world, GeneratedQueryAccess access)
    {
        WorldStorageResourceKey key = Key(access);
        return access.Mode == GeneratedQueryMode.ReadWrite
            ? WorldStorageJobResources.Write(world, key)
            : WorldStorageJobResources.Read(world, key);
    }

    private static JobResourceAccess RangeAccess(
        World world,
        GeneratedQueryAccess access,
        long start,
        long length)
    {
        WorldStorageResourceKey key = Key(access);
        return access.Mode == GeneratedQueryMode.ReadWrite
            ? WorldStorageJobResources.Write(world, key, start, length)
            : WorldStorageJobResources.Read(world, key, start, length);
    }

    private static JobResourceAccess ReadRangeAccess(
        World world,
        GeneratedQueryAccess access,
        long start,
        long length) =>
        WorldStorageJobResources.Read(world, Key(access), start, length);

    private static WorldStorageResourceKey Key(GeneratedQueryAccess access) =>
        new(
            access.Storage switch
            {
                GeneratedQueryStorage.Table => WorldStorageKind.Table,
                GeneratedQueryStorage.Buffer => WorldStorageKind.Buffer,
                GeneratedQueryStorage.Sparse => WorldStorageKind.Sparse,
                _ => throw new ArgumentOutOfRangeException(nameof(access)),
            },
            access.ComponentId);

    private readonly struct SerialJob<TJob, TAdapter> : IJob
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;
        private readonly TAdapter _adapter;
        private readonly GeneratedQueryAccessDescriptor _descriptor;
        private readonly uint _lastSystemVersion;

        internal SerialJob(
            World world,
            QueryHandle query,
            in TJob job,
            TAdapter adapter,
            GeneratedQueryAccessDescriptor descriptor,
            uint lastSystemVersion)
        {
            _world = world;
            _query = query;
            _job = job;
            _adapter = adapter;
            _descriptor = descriptor;
            _lastSystemVersion = lastSystemVersion;
        }

        public void Execute()
        {
            var state = new SerialState<TJob, TAdapter>(
                _world,
                _job,
                _adapter,
                _descriptor);
            _world.ExecuteQuery(
                _query,
                _lastSystemVersion,
                currentSystemVersion: 0,
                ref state,
                static (QueryCursor cursor, ref SerialState<TJob, TAdapter> state) =>
                    ExecuteCursor(cursor, ref state));
        }
    }

    private readonly struct SerialQueryCaptureJob<TJob, TAdapter> : IJob
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        private readonly World _world;
        private readonly TJob _job;
        private readonly TAdapter _adapter;
        private readonly GeneratedQueryAccessDescriptor _descriptor;
        private readonly JobEntityScheduleOptions _options;

        internal SerialQueryCaptureJob(
            World world,
            in TJob job,
            TAdapter adapter,
            GeneratedQueryAccessDescriptor descriptor,
            JobEntityScheduleOptions options)
        {
            _world = world;
            _job = job;
            _adapter = adapter;
            _descriptor = descriptor;
            _options = options;
        }

        public void Execute()
        {
            JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(_world));
            QueryHandle query = _descriptor.Resolve(_world);
            JobResourceAccess[] accesses = BuildWholeAccesses(_world, _descriptor);
            var scheduled = new SerialJob<TJob, TAdapter>(
                _world,
                query,
                _job,
                _adapter,
                _descriptor,
                _options.LastSystemVersion);
            // Schedule registers the child accesses and attaches the child synchronously while
            // this capture still owns topology-read work. A writer registered first may precede
            // the child, but it cannot execute before registration; a writer registered later
            // observes the child. Either ordering has no unguarded capture-to-child interval.
            JobSystem.Schedule(scheduled, accesses, _options.JobOptions);
        }
    }

    private readonly struct PacketCaptureJob<TJob, TAdapter> : IJob
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        private readonly World _world;
        private readonly TJob _job;
        private readonly TAdapter _adapter;
        private readonly GeneratedQueryAccessDescriptor _descriptor;
        private readonly JobEntityScheduleOptions _options;

        internal PacketCaptureJob(
            World world,
            TJob job,
            TAdapter adapter,
            GeneratedQueryAccessDescriptor descriptor,
            JobEntityScheduleOptions options)
        {
            _world = world;
            _job = job;
            _adapter = adapter;
            _descriptor = descriptor;
            _options = options;
        }

        public void Execute()
        {
            JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(_world));
            QueryHandle query = _descriptor.Resolve(_world);
            StableQueryPacketSet packets = CapturePackets(
                _world,
                query,
                _options.RowsPerPacket,
                _options.LastSystemVersion);
            JobResourceAccess[] accesses = BuildPacketAccesses(_world, _descriptor, packets);
            var scheduled = new ParallelJob<TJob, TAdapter>(
                packets,
                _job,
                _adapter,
                _descriptor,
                new JobEntityExecutionVersion());
            // Resource registration plus scope attachment complete before ScheduleParallel
            // returns. The capture's topology-read work grant is released only after Execute
            // returns, so a topology writer cannot execute in the handoff interval. The returned
            // capture handle remains incomplete until this attached dispatch completes.
            JobSystem.ScheduleParallel(
                scheduled,
                packets.Packets.Length,
                batchSize: 1,
                accesses,
                _options.JobOptions);
        }
    }

    private struct SerialState<TJob, TAdapter>
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        internal SerialState(
            World world,
            TJob job,
            TAdapter adapter,
            GeneratedQueryAccessDescriptor descriptor)
        {
            World = world;
            Job = job;
            Adapter = adapter;
            Descriptor = descriptor;
            ExecutionVersion = 0;
            HasExecutionVersion = false;
        }

        internal World World;
        internal TJob Job;
        internal TAdapter Adapter;
        internal GeneratedQueryAccessDescriptor Descriptor;
        internal uint ExecutionVersion;
        internal bool HasExecutionVersion;

        internal uint GetExecutionVersion()
        {
            if (!Descriptor.HasWorldWrites)
                return 0;

            if (!HasExecutionVersion)
            {
                // The serial child already owns its complete data access set. Allocate the
                // logical write version only when the first writable row is about to enter user
                // code, so empty, fully filtered, and read-only queries have no false epoch.
                ExecutionVersion = World.AcquireSystemVersion();
                HasExecutionVersion = true;
            }
            return ExecutionVersion;
        }
    }

    private readonly struct ParallelJob<TJob, TAdapter> : IJobParallelFor
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        private readonly StableQueryPacketSet _packets;
        private readonly TJob _job;
        private readonly TAdapter _adapter;
        private readonly GeneratedQueryAccessDescriptor _descriptor;
        private readonly JobEntityExecutionVersion _executionVersion;

        internal ParallelJob(
            StableQueryPacketSet packets,
            in TJob job,
            TAdapter adapter,
            GeneratedQueryAccessDescriptor descriptor,
            JobEntityExecutionVersion executionVersion)
        {
            _packets = packets;
            _job = job;
            _adapter = adapter;
            _descriptor = descriptor;
            _executionVersion = executionVersion;
        }

        public void Execute(int index)
        {
            if (_packets.World.PublishedStructureEpoch != _packets.Proof.StructureEpoch)
            {
                throw new InvalidOperationException(
                    "A stable query packet was executed against a different World structure generation.");
            }
            JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(_packets.World));
            if (_packets.World.PublishedTopologyRevision != _packets.Proof.TopologyRevision)
            {
                throw new InvalidOperationException(
                    "A stable query packet capture became stale before packet execution; no rows from this packet were borrowed.");
            }

            QueryPacket packet = _packets.Packets[index];
            RequirePacketAccesses(_packets.World, _descriptor, in packet);
            if (packet.Match.ChunkColumns.Length != 0 &&
                !packet.Match.MatchesChunkOnly(packet.Chunk, _packets.LastSystemVersion))
            {
                return;
            }
            SomeEngine.ECS.Owners.Iteration iteration =
                _packets.World.ActiveStructureRoot.Iteration;
            iteration.BeginQueryBorrow();
            try
            {
                TJob job = _job;
                TAdapter adapter = _adapter;
                uint currentSystemVersion = 0;
                bool hasExecutionVersion = false;
                int end = packet.Range.RowStart + packet.Range.RowCount;
                for (int rowIndex = packet.Range.RowStart; rowIndex < end; rowIndex++)
                {
                    if (!packet.Match.MatchesRow(
                            packet.Chunk,
                            rowIndex,
                            _packets.LastSystemVersion))
                        continue;
                    if (_descriptor.HasWorldWrites && !hasExecutionVersion)
                    {
                        // The complete parallel range owner is already admitted. The first
                        // writable packet that will actually enter a body publishes one version
                        // for all matching packets. Read-only and filtered packets never advance
                        // World time.
                        currentSystemVersion = _executionVersion.Get(_packets.World);
                        hasExecutionVersion = true;
                    }
                    var row = new JobEntityRow(
                        _packets.World,
                        packet.Match,
                        packet.Chunk,
                        _descriptor,
                        rowIndex,
                        currentSystemVersion);
                    adapter.Execute(ref job, ref row);
                }
            }
            finally
            {
                iteration.EndQueryBorrow();
            }
        }
    }

    private static void RequirePacketAccesses(
        World world,
        GeneratedQueryAccessDescriptor descriptor,
        in QueryPacket packet)
    {
        StableQueryPacketRange packetRange = packet.Range;
        long tableStart = StableQueryPacketAddress.Address(in packetRange);
        for (int i = 0; i < descriptor.AccessCount; i++)
        {
            GeneratedQueryAccess access = descriptor.GetAccess(i);
            if (access.Storage == GeneratedQueryStorage.Sparse)
            {
                if (!access.HasDirectAccess)
                    continue;
                int end = packet.Range.RowStart + packet.Range.RowCount;
                for (int row = packet.Range.RowStart; row < end; row++)
                {
                    JobSystem.RequireCurrentAccess(
                        RangeAccess(world, access, packet.Chunk.Entities[row].Index, 1));
                }
                continue;
            }

            QueryTermFilter rowFilters = access.Filters & ~QueryTermFilter.ChunkChanged;
            if (access.HasDirectAccess)
            {
                JobSystem.RequireCurrentAccess(
                    RangeAccess(world, access, tableStart, packet.Range.RowCount));
            }
            else if (rowFilters != QueryTermFilter.None)
            {
                JobSystem.RequireCurrentAccess(
                    ReadRangeAccess(world, access, tableStart, packet.Range.RowCount));
            }

            if ((access.Filters & QueryTermFilter.ChunkChanged) != 0)
            {
                var chunkRange = new StableQueryPacketRange(
                    packet.Range.PersistentChunkId,
                    rowStart: 0,
                    packet.Chunk.Count,
                    packet.Chunk.Count);
                JobSystem.RequireCurrentAccess(
                    ReadRangeAccess(
                        world,
                        access,
                        StableQueryPacketAddress.Address(in chunkRange),
                        chunkRange.RowCount));
            }
        }
    }

    private static void ExecuteCursor<TJob, TAdapter>(
        QueryCursor cursor,
        ref SerialState<TJob, TAdapter> state)
        where TJob : unmanaged, IJobEntity
        where TAdapter : unmanaged, IGeneratedJobEntityAdapter<TJob>
    {
        QueryChunkEnumerator<NoSharedFilter> chunks = cursor.Chunks;
        while (chunks.MoveNext())
        {
            QueryArchetypeMatch match = chunks.CurrentMatch;
            Chunk chunk = chunks.CurrentChunk;
            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
            {
                if (!match.MatchesRow(chunk, rowIndex, cursor.LastSystemVersion))
                    continue;
                uint currentSystemVersion = state.GetExecutionVersion();
                var row = new JobEntityRow(
                    state.World,
                    match,
                    chunk,
                    state.Descriptor,
                    rowIndex,
                    currentSystemVersion);
                state.Adapter.Execute(ref state.Job, ref row);
            }
        }
    }

    private sealed class JobEntityExecutionVersion
    {
        private readonly Lock _gate = new();
        private bool _initialized;
        private uint _version;

        internal uint Get(World world)
        {
            if (Volatile.Read(ref _initialized))
                return _version;

            lock (_gate)
            {
                if (!_initialized)
                {
                    _version = world.AcquireSystemVersion();
                    Volatile.Write(ref _initialized, true);
                }
                return _version;
            }
        }
    }

}
