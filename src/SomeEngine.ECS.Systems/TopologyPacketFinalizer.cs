using System.Buffers;
using System.Runtime.ExceptionServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// A topology packet callback that edits a detached image of canonical Parent values.
/// </summary>
/// <remarks>
/// The spans are valid only for the duration of <see cref="Execute"/>. Packet callbacks never
/// borrow the live World and cannot publish topology. After every packet succeeds, one serial
/// finalizer validates exact captured query membership and logical Parent preimages, then
/// publishes the complete image atomically. Unrelated edits and final-state-equivalent ABA may
/// serialize before that finalizer; a different membership or Parent preimage faults it.
/// </remarks>
public interface IParentTopologyPacketJob<TDomain>
    where TDomain : IHierarchyDomain
{
    void Execute(
        in TopologyPacketContext packet,
        ReadOnlySpan<Entity> entities,
        Span<Parent<TDomain>> parents);
}

/// <summary>Stable identity and physical row range of one topology packet.</summary>
public readonly struct TopologyPacketContext
{
    internal TopologyPacketContext(
        int packetIndex,
        StableQueryPacketRange range,
        uint lastSystemVersion)
    {
        PacketIndex = packetIndex;
        Range = range;
        LastSystemVersion = lastSystemVersion;
    }

    public int PacketIndex { get; }

    public StableQueryPacketRange Range { get; }

    public uint LastSystemVersion { get; }
}

/// <summary>Explicit packet granularity and version filters for a topology transaction.</summary>
public readonly struct TopologyPacketScheduleOptions
{
    public TopologyPacketScheduleOptions(
        int rowsPerPacket = 0,
        uint lastSystemVersion = 0,
        JobScheduleOptions jobOptions = default)
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));

        RowsPerPacket = rowsPerPacket;
        LastSystemVersion = lastSystemVersion;
        JobOptions = jobOptions;
    }

    /// <summary>Zero means one complete physical chunk per packet.</summary>
    public int RowsPerPacket { get; }

    public uint LastSystemVersion { get; }

    public JobScheduleOptions JobOptions { get; }
}

/// <summary>
/// Asynchronous topology transaction. <see cref="Handle"/> represents dependency, capture,
/// every packet, final-image validation, and the single publication.
/// </summary>
public sealed class TopologyFinalization
{
    private readonly TopologyOperationState _state;

    internal TopologyFinalization(TopologyOperationState state, JobHandle handle)
    {
        _state = state;
        Handle = handle;
    }

    public JobHandle Handle { get; }

    /// <summary>
    /// Returns the captured proof after successful transaction completion. Use
    /// <see cref="GetPartition"/> when waiting is desired explicitly.
    /// </summary>
    public StableQueryPartitionProof Partition
    {
        get
        {
            if (!Handle.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Topology partition proof is available after the transaction handle completes.");
            }
            Handle.Complete();
            return _state.RequireSuccessfulProof();
        }
    }

    public StableQueryPartitionProof GetPartition()
    {
        Handle.Complete();
        return _state.RequireSuccessfulProof();
    }
}

/// <summary>
/// Schedules parallel canonical-Parent packet editing with one fault-atomic topology finalizer.
/// </summary>
public static class TopologyPacketFinalizer<TDomain>
    where TDomain : IHierarchyDomain
{
    public static TopologyFinalization Schedule<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        TopologyPacketScheduleOptions options = default,
        JobHandle dependency = default)
        where TJob : unmanaged, IParentTopologyPacketJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);

        QueryDefinition definition = world.PublishedStructureRoot.Queries.Get(query).Definition;
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<Parent<TDomain>>(definition);
        var state = new TopologyOperationState();
        var captureJob = new CaptureAndScheduleJob<TJob>(
            world,
            query,
            job,
            options,
            state);
        JobHandle operation = JobSystem.Schedule(
            captureJob,
            BuildCaptureAccesses(world, definition),
            options.JobOptions,
            dependency);
        try
        {
            JobHandle terminal = JobSystem.ScheduleFinally(
                new TopologyCompletionAdapter(state, operation),
                options.JobOptions,
                operation);
            return new TopologyFinalization(state, terminal);
        }
        catch (Exception exception)
        {
            RecoverMissingTerminal(state, operation, exception);
            throw new InvalidOperationException("Unreachable topology terminal recovery path.");
        }
    }

    private static void RecoverMissingTerminal(
        TopologyOperationState state,
        JobHandle operation,
        Exception terminalScheduleFault)
    {
        Exception? operationFault = null;
        try
        {
            operation.Complete();
        }
        catch (Exception exception)
        {
            operationFault = exception;
        }
        finally
        {
            state.MarkFailed();
        }

        if (operationFault is not null)
        {
            throw new AggregateException(
                "Topology operation and terminal-observer scheduling both failed.",
                terminalScheduleFault,
                operationFault);
        }

        ExceptionDispatchInfo.Capture(terminalScheduleFault).Throw();
    }

    private readonly struct TopologyCompletionAdapter : IJob
    {
        private readonly TopologyOperationState _state;
        private readonly JobHandle _operation;

        internal TopologyCompletionAdapter(
            TopologyOperationState state,
            JobHandle operation)
        {
            _state = state;
            _operation = operation;
        }

        public void Execute()
        {
            try
            {
                _operation.Complete();
                _state.MarkSucceeded();
            }
            catch
            {
                _state.MarkFailed();
                throw;
            }
        }
    }

    private static JobResourceAccess[] BuildPacketAccesses(
        ParentTopologyStage<TDomain> stage)
    {
        var accesses = new JobResourceAccess[1 + checked(stage.PacketCount * 2)];
        accesses[0] = stage.EntityReadAccess();
        for (int i = 0; i < stage.PacketCount; i++)
        {
            StableQueryPacketRange range = stage.Proof.GetPacket(i);
            int accessOffset = 1 + checked(i * 2);
            accesses[accessOffset] = stage.CapturedParentReadAccess(
                stage.Proof.GetRowOffset(i),
                range.RowCount);
            accesses[accessOffset + 1] = stage.PacketEditWriteAccess(i);
        }
        return accesses;
    }

    private static JobResourceAccess[] BuildCaptureAccesses(
        World world,
        QueryDefinition definition)
    {
        ReadOnlySpan<WorldJobStorageAccess> queryAccesses =
            definition.JobStorageAccesses.Span;
        var result = new List<JobResourceAccess>(queryAccesses.Length + 1)
        {
            RelationshipJobAccess.TopologyRead(world),
        };
        for (int i = 0; i < queryAccesses.Length; i++)
        {
            WorldJobStorageAccess access = queryAccesses[i];
            JobResourceAccess read = WorldStorageJobResources.Read(
                world,
                new WorldStorageResourceKey(access.Kind, access.ComponentId));
            bool duplicate = false;
            for (int existing = 0; existing < result.Count; existing++)
            {
                if (result[existing].Covers(read))
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                result.Add(read);
        }
        return result.ToArray();
    }

    private readonly struct CaptureAndScheduleJob<TJob> : IJob
        where TJob : unmanaged, IParentTopologyPacketJob<TDomain>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;
        private readonly TopologyPacketScheduleOptions _options;
        private readonly TopologyOperationState _state;

        internal CaptureAndScheduleJob(
            World world,
            QueryHandle query,
            in TJob job,
            TopologyPacketScheduleOptions options,
            TopologyOperationState state)
        {
            _world = world;
            _query = query;
            _job = job;
            _options = options;
            _state = state;
        }

        public void Execute()
        {
            ParentTopologyStage<TDomain> stage = TopologyStablePacketCapture.Capture<TDomain>(
                _world,
                _query,
                _options.RowsPerPacket,
                _options.LastSystemVersion);
            _state.SetProof(stage.Proof);
            if (stage.PacketCount == 0)
                return;

            var packetJob = new ParentPacketJob<TJob>(stage, _job);
            JobHandle packets = JobSystem.ScheduleParallel(
                packetJob,
                stage.PacketCount,
                batchSize: 1,
                BuildPacketAccesses(stage),
                _options.JobOptions);

            // This no-resource continuation becomes runnable only after staging packets.
            // Registering the topology writer from here would put it on the frontier too
            // early and unnecessarily block unrelated World work while packets are running.
            var finalizerLauncher = new FinalizerLauncherJob(
                stage,
                _options.JobOptions);
            JobSystem.Schedule(
                finalizerLauncher,
                _options.JobOptions,
                packets);
        }
    }

    private readonly struct FinalizerLauncherJob : IJob
    {
        private readonly ParentTopologyStage<TDomain> _stage;
        private readonly JobScheduleOptions _options;

        internal FinalizerLauncherJob(
            ParentTopologyStage<TDomain> stage,
            JobScheduleOptions options)
        {
            _stage = stage;
            _options = options;
        }

        public void Execute()
        {
            if (!_stage.HasChanges())
                return;

            var finalizer = new ParentFinalizerJob(_stage);
            WorldStorageJobSchedule.ScheduleTopologyWrite(
                _stage.World,
                HierarchyJobAccess<TDomain>.ParentWrite(_stage.World),
                in finalizer,
                _options);
        }
    }

    private readonly struct ParentPacketJob<TJob> : IJobParallelFor
        where TJob : unmanaged, IParentTopologyPacketJob<TDomain>
    {
        private readonly ParentTopologyStage<TDomain> _stage;
        private readonly TJob _job;

        internal ParentPacketJob(ParentTopologyStage<TDomain> stage, in TJob job)
        {
            _stage = stage;
            _job = job;
        }

        public void Execute(int index)
        {
            StableQueryPacketRange range = _stage.Proof.GetPacket(index);
            int valueOffset = _stage.Proof.GetRowOffset(index);
            JobSystem.RequireCurrentAccess(_stage.EntityReadAccess());
            JobSystem.RequireCurrentAccess(
                _stage.CapturedParentReadAccess(valueOffset, range.RowCount));
            JobSystem.RequireCurrentAccess(_stage.PacketEditWriteAccess(index));

            Parent<TDomain>[] rented =
                ArrayPool<Parent<TDomain>>.Shared.Rent(range.RowCount);
            Span<Parent<TDomain>> candidate = rented.AsSpan(0, range.RowCount);
            try
            {
                ReadOnlySpan<Parent<TDomain>> captured =
                    _stage.CapturedParents.Slice(valueOffset, range.RowCount);
                captured.CopyTo(candidate);

                TJob state = _job;
                var context = new TopologyPacketContext(
                    index,
                    range,
                    _stage.LastSystemVersion);
                state.Execute(
                    in context,
                    _stage.Entities.Slice(valueOffset, range.RowCount),
                    candidate);

                int changedCount = 0;
                for (int row = 0; row < candidate.Length; row++)
                {
                    if (candidate[row].Value != captured[row].Value)
                        changedCount++;
                }

                ParentTopologyEdit[] edits = changedCount == 0
                    ? Array.Empty<ParentTopologyEdit>()
                    : new ParentTopologyEdit[changedCount];
                int editIndex = 0;
                for (int row = 0; row < candidate.Length; row++)
                {
                    if (candidate[row].Value != captured[row].Value)
                    {
                        edits[editIndex++] = new ParentTopologyEdit(
                            row,
                            candidate[row].Value);
                    }
                }
                _stage.PublishPacketEdits(index, edits);
            }
            finally
            {
                candidate.Clear();
                ArrayPool<Parent<TDomain>>.Shared.Return(rented);
            }
        }
    }

    private readonly struct ParentFinalizerJob : IJob
    {
        private readonly ParentTopologyStage<TDomain> _stage;

        internal ParentFinalizerJob(ParentTopologyStage<TDomain> stage)
        {
            _stage = stage;
        }

        public void Execute()
        {
            ValidateLivePreimages();
            using StructuralMutationScope mutation = _stage.World.BeginStructuralMutation();
            // The publication version belongs to the admitted topology writer, not to packet
            // staging. An unrelated writer may legally serialize between capture and this job.
            uint commitSystemVersion = _stage.World.AcquireSystemVersion();
            for (int packet = 0; packet < _stage.PacketCount; packet++)
            {
                int valueOffset = _stage.Proof.GetRowOffset(packet);
                ReadOnlySpan<ParentTopologyEdit> edits =
                    _stage.RequirePacketEdits(packet);
                for (int editIndex = 0; editIndex < edits.Length; editIndex++)
                {
                    ParentTopologyEdit edit = edits[editIndex];
                    int row = checked(valueOffset + edit.LocalRow);
                    Entity entity = _stage.Entities[row];
                    var replacement = new Parent<TDomain>(edit.Replacement);
                    _stage.World.ReplaceRelationshipComponent(
                        entity,
                        in replacement,
                        commitSystemVersion);
                }
            }

            // Maintain validates the complete staged forest once and publishes every affected
            // inverse shard only inside the detached candidate. The live World observes only the
            // root publication below.
            Hierarchy<TDomain>.Maintain(_stage.World);
            mutation.Commit();
        }

        private void ValidateLivePreimages()
        {
            ValidateQueryMembership();
            for (int i = 0; i < _stage.Entities.Length; i++)
            {
                Entity entity = _stage.Entities[i];
                if (!_stage.World.IsAlive(entity) ||
                    !_stage.World.Has<Parent<TDomain>>(entity))
                {
                    throw new InvalidOperationException(
                        $"Topology packet entity {entity} no longer has its captured Parent component.");
                }

                Parent<TDomain> current = _stage.World.Read<Parent<TDomain>>(entity);
                if (current.Value != _stage.CapturedParents[i].Value)
                {
                    throw new InvalidOperationException(
                        $"Canonical Parent for {entity} changed after topology packet capture.");
                }
            }
        }

        private void ValidateQueryMembership()
        {
            // The detached edit is a transaction over the exact query membership captured after
            // its semantic dependency. Unrelated topology edits may merge, but adding or removing
            // a selected entity would otherwise make the packet image an incomplete query result.
            var remaining = new HashSet<Entity>(_stage.Entities.Length);
            for (int index = 0; index < _stage.Entities.Length; index++)
                remaining.Add(_stage.Entities[index]);
            if (remaining.Count != _stage.Entities.Length)
                throw new InvalidOperationException("Topology packet capture contains duplicate entities.");

            QueryState state = _stage.World.ActiveStructureRoot.Queries.Get(_stage.Query).State;
            ReadOnlySpan<QueryArchetypeMatch> matches = state.Matches;
            for (int matchIndex = 0; matchIndex < matches.Length; matchIndex++)
            {
                QueryArchetypeMatch match = matches[matchIndex];
                ReadOnlySpan<Chunk> chunks = match.Archetype.Chunks;
                for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
                {
                    Chunk chunk = chunks[chunkIndex];
                    if (chunk.Count <= 0 ||
                        !match.MatchesChanged(chunk, _stage.LastSystemVersion))
                    {
                        continue;
                    }

                    for (int row = 0; row < chunk.Count; row++)
                    {
                        if (!remaining.Remove(chunk.Entities[row]))
                        {
                            throw new InvalidOperationException(
                                "Topology packet query membership changed after capture.");
                        }
                    }
                }
            }

            if (remaining.Count != 0)
            {
                throw new InvalidOperationException(
                    "Topology packet query membership changed after capture.");
            }
        }
    }
}

internal static class TopologyStablePacketCapture
{
    internal static ParentTopologyStage<TDomain> Capture<TDomain>(
        World world,
        QueryHandle query,
        int rowsPerPacket,
        uint lastSystemVersion)
        where TDomain : IHierarchyDomain
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));

        QueryState state = world.ActiveStructureRoot.Queries.Get(query).State;
        int parentComponentId = ComponentMetadata<Parent<TDomain>>.Id;
        ReadOnlySpan<QueryArchetypeMatch> matches = state.Matches;
        int totalRows = 0;
        int packetCount = 0;
        for (int matchIndex = 0; matchIndex < matches.Length; matchIndex++)
        {
            QueryArchetypeMatch match = matches[matchIndex];
            if (!match.TryGetAccess(parentComponentId, out QueryColumnAccess firstPassDefinition) ||
                !firstPassDefinition.Access.CanWrite())
            {
                throw new InvalidOperationException(
                    $"{typeof(Parent<TDomain>).Name} was not declared for query write access.");
            }

            ReadOnlySpan<Chunk> chunks = match.Archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                Chunk chunk = chunks[chunkIndex];
                int count = chunk.Count;
                if (count <= 0 || !match.MatchesChanged(chunk, lastSystemVersion))
                    continue;

                if (chunk.PersistentIdentity <= 0 ||
                    chunk.PersistentIdentity >
                    long.MaxValue / StableQueryPacketAddress.RowsPerChunkStride)
                {
                    throw new InvalidOperationException(
                        "Stable query chunk identity space is exhausted.");
                }
                int packetRows = rowsPerPacket == 0 ? count : rowsPerPacket;
                totalRows = checked(totalRows + count);
                packetCount = checked(packetCount + 1 + ((count - 1) / packetRows));
            }
        }

        var entities = new Entity[totalRows];
        var capturedParents = new Parent<TDomain>[totalRows];
        var ranges = new StableQueryPacketRange[packetCount];
        int valueOffset = 0;
        int packetIndex = 0;
        for (int matchIndex = 0; matchIndex < matches.Length; matchIndex++)
        {
            QueryArchetypeMatch match = matches[matchIndex];
            if (!match.TryGetAccess(parentComponentId, out QueryColumnAccess access) ||
                !access.Access.CanWrite())
            {
                throw new InvalidOperationException(
                    $"{typeof(Parent<TDomain>).Name} was not declared for query write access.");
            }

            ReadOnlySpan<Chunk> chunks = match.Archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                Chunk chunk = chunks[chunkIndex];
                int count = chunk.Count;
                if (count <= 0 || !match.MatchesChanged(chunk, lastSystemVersion))
                    continue;

                long persistentIdentity = chunk.PersistentIdentity;
                if (persistentIdentity <= 0 ||
                    persistentIdentity >
                    long.MaxValue / StableQueryPacketAddress.RowsPerChunkStride)
                {
                    throw new InvalidOperationException(
                        "Stable query chunk identity space is exhausted.");
                }

                int packetRows = rowsPerPacket == 0 ? count : rowsPerPacket;
                for (int start = 0; start < count; start += packetRows)
                {
                    int length = Math.Min(packetRows, count - start);
                    ranges[packetIndex++] = new StableQueryPacketRange(
                        persistentIdentity,
                        start,
                        length,
                        chunkRowCount: count);
                    for (int row = start; row < start + length; row++)
                    {
                        entities[valueOffset] = chunk.Entities[row];
                        capturedParents[valueOffset] =
                            chunk.ReadComponent<Parent<TDomain>>(access.ColumnIndex, row);
                        valueOffset++;
                    }
                }
            }
        }
        if (valueOffset != totalRows || packetIndex != packetCount)
            throw new InvalidOperationException("Stable topology capture changed between count and fill passes.");

        var proof = new StableQueryPartitionProof(
            ranges,
            world.PublishedStructureEpoch,
            world.PublishedTopologyRevision);
        return new ParentTopologyStage<TDomain>(
            world,
            query,
            entities,
            capturedParents,
            proof,
            lastSystemVersion);
    }

}

internal readonly struct ParentTopologyEdit
{
    internal ParentTopologyEdit(int localRow, Entity replacement)
    {
        LocalRow = localRow;
        Replacement = replacement;
    }

    internal int LocalRow { get; }

    internal Entity Replacement { get; }
}

internal sealed class ParentTopologyStage<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly Entity[] _entities;
    private readonly Parent<TDomain>[] _capturedParents;
    private readonly ParentTopologyEdit[]?[] _packetEdits;

    internal ParentTopologyStage(
        World world,
        QueryHandle query,
        Entity[] ownedEntities,
        Parent<TDomain>[] ownedCapturedParents,
        StableQueryPartitionProof proof,
        uint lastSystemVersion)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(ownedEntities);
        ArgumentNullException.ThrowIfNull(ownedCapturedParents);
        ArgumentNullException.ThrowIfNull(proof);
        if (ownedEntities.Length != proof.TotalRowCount ||
            ownedCapturedParents.Length != proof.TotalRowCount)
        {
            throw new InvalidOperationException(
                "Topology staging arrays must exactly cover the stable query partition.");
        }

        World = world;
        Query = query;
        _entities = ownedEntities;
        _capturedParents = ownedCapturedParents;
        _packetEdits = new ParentTopologyEdit[]?[proof.PacketCount];
        Proof = proof;
        LastSystemVersion = lastSystemVersion;
    }

    internal World World { get; }

    internal QueryHandle Query { get; }

    internal ReadOnlySpan<Entity> Entities => _entities;

    internal ReadOnlySpan<Parent<TDomain>> CapturedParents => _capturedParents;

    internal StableQueryPartitionProof Proof { get; }

    internal uint LastSystemVersion { get; }

    internal int PacketCount => Proof.PacketCount;

    internal JobResourceAccess EntityReadAccess() =>
        JobResourceAccess.Read(_entities);

    internal JobResourceAccess CapturedParentReadAccess(int offset, int length) =>
        JobResourceAccess.Read(_capturedParents, offset, length);

    internal JobResourceAccess PacketEditWriteAccess(int packetIndex) =>
        JobResourceAccess.Write(_packetEdits, packetIndex, 1);

    internal void PublishPacketEdits(int packetIndex, ParentTopologyEdit[] ownedEdits)
    {
        ArgumentNullException.ThrowIfNull(ownedEdits);
        if ((uint)packetIndex >= (uint)_packetEdits.Length)
            throw new ArgumentOutOfRangeException(nameof(packetIndex));

        int rowCount = Proof.GetPacket(packetIndex).RowCount;
        int previousRow = -1;
        for (int i = 0; i < ownedEdits.Length; i++)
        {
            int localRow = ownedEdits[i].LocalRow;
            if ((uint)localRow >= (uint)rowCount)
            {
                throw new InvalidOperationException(
                    "A topology packet edit addresses a row outside its proven packet range.");
            }
            if (localRow <= previousRow)
            {
                throw new InvalidOperationException(
                    "Topology packet edits must use unique, strictly increasing local rows.");
            }
            previousRow = localRow;
        }

        if (Interlocked.CompareExchange(
                ref _packetEdits[packetIndex],
                ownedEdits,
                null) is not null)
        {
            throw new InvalidOperationException(
                "A topology packet published its detached edits more than once.");
        }
    }

    internal ReadOnlySpan<ParentTopologyEdit> RequirePacketEdits(int packetIndex)
    {
        if ((uint)packetIndex >= (uint)_packetEdits.Length)
            throw new ArgumentOutOfRangeException(nameof(packetIndex));
        return Volatile.Read(ref _packetEdits[packetIndex])
            ?? throw new InvalidOperationException(
                "A successful topology packet did not publish its detached edits.");
    }

    internal bool HasChanges()
    {
        for (int i = 0; i < _packetEdits.Length; i++)
        {
            if (RequirePacketEdits(i).Length != 0)
                return true;
        }
        return false;
    }
}
