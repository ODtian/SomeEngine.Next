using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Pipeline-owned structural classification contract. Implementations interpret their own
/// material, pass, geometry, and specialization components; the shared render infrastructure
/// only transports the resulting buffer elements. Count and Write are invoked from separate
/// parallel passes and must therefore be pure and deterministic for one read snapshot.
/// </summary>
public interface IRenderEntityClassifier<TMembership>
    where TMembership : struct, IBufferElement
{
    int Count(ReadOnlyQueryPacket packet, int row);

    void Write(
        ReadOnlyQueryPacket packet,
        int row,
        ref RenderEntityMembershipWriter<TMembership> memberships);
}

/// <summary>
/// Exact-count writer for one entity's pipeline-owned memberships. A classifier cannot append to
/// another entity or retain this callback-scoped destination.
/// </summary>
public ref struct RenderEntityMembershipWriter<TMembership>
    where TMembership : struct, IBufferElement
{
    private readonly Span<TMembership> _destination;
    private int _count;

    internal RenderEntityMembershipWriter(Span<TMembership> destination)
    {
        _destination = destination;
        _count = 0;
    }

    public int Capacity => _destination.Length;

    public int Count => _count;

    public void Add(in TMembership membership)
    {
        if ((uint)_count >= (uint)_destination.Length)
        {
            throw new InvalidOperationException(
                "The render classifier wrote more memberships than it counted.");
        }

        _destination[_count++] = membership;
    }

    internal void RequireComplete()
    {
        if (_count != _destination.Length)
        {
            throw new InvalidOperationException(
                $"The render classifier counted {_destination.Length} memberships but wrote {_count}.");
        }
    }
}

/// <summary>
/// Maintains one concrete pipeline's persistent structural classification on RenderWorld
/// entities. The entity remains the only instance identity; memberships are an ordinary
/// pipeline-owned dynamic buffer and may contain several material passes. This system does not
/// create draw bins, instance batches, or global render identities.
/// </summary>
public sealed class RenderEntityClassificationSystem<TClassifier, TMembership> :
    ISystem<RenderPrepareSystemContext>
    where TClassifier : struct, IRenderEntityClassifier<TMembership>
    where TMembership : struct, IBufferElement
{
    private readonly QueryDefinition _initialCandidates;
    private readonly QueryDefinition _invalidatedEntities;
    private readonly TClassifier _classifier;
    private readonly int _rowsPerPacket;
    private readonly JobScheduleOptions _jobOptions;
    private QueryHandle _initialQuery;
    private QueryHandle _invalidatedQuery;
    private QueryHandle _ownedMembershipsQuery;
    private bool _created;
    private bool _initialized;

    public RenderEntityClassificationSystem(
        QueryDefinition initialCandidates,
        QueryDefinition invalidatedEntities,
        in TClassifier classifier,
        int rowsPerPacket = 0,
        JobScheduleOptions jobOptions = default)
    {
        ArgumentNullException.ThrowIfNull(initialCandidates);
        ArgumentNullException.ThrowIfNull(invalidatedEntities);
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));

        _initialCandidates = initialCandidates;
        _invalidatedEntities = invalidatedEntities;
        _classifier = classifier;
        _rowsPerPacket = rowsPerPacket;
        _jobOptions = jobOptions;
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        _initialQuery = context.World.Query(_initialCandidates);
        _invalidatedQuery = context.World.Query(_invalidatedEntities);
        _ownedMembershipsQuery = context.World.Query(
            new QueryDefinitionBuilder().ReadBuffer<TMembership>().Build());
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        QueryHandle query = _initialized ? _invalidatedQuery : _initialQuery;
        uint lastSystemVersion = _initialized ? context.LastSystemVersion : 0;
        Classify(context.World, query, lastSystemVersion);
        _initialized = true;
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;

        try
        {
            RemoveOwnedMemberships(context.World);
        }
        finally
        {
            context.World.ReleaseQuery(_ownedMembershipsQuery);
            context.World.ReleaseQuery(_invalidatedQuery);
            context.World.ReleaseQuery(_initialQuery);
            _created = false;
            _initialized = false;
        }
    }

    private void Classify(
        RenderWorld world,
        QueryHandle query,
        uint lastSystemVersion)
    {
        var snapshot = new ClassificationSnapshot(
            _classifier,
            _rowsPerPacket,
            _jobOptions);
        try
        {
            world.ExecuteReadSnapshot(
                query,
                lastSystemVersion,
                ref snapshot,
                static (QueryCursor cursor, ref ClassificationSnapshot state) =>
                    state.Record(cursor));
            snapshot.Playback();
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    private void RemoveOwnedMemberships(RenderWorld world)
    {
        var snapshot = new RemovalSnapshot(_rowsPerPacket, _jobOptions);
        try
        {
            world.ExecuteReadSnapshot(
                _ownedMembershipsQuery,
                ref snapshot,
                static (QueryCursor cursor, ref RemovalSnapshot state) =>
                    state.Record(cursor));
            snapshot.Playback();
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    private struct ClassificationSnapshot : IDisposable
    {
        private readonly TClassifier _classifier;
        private readonly int _rowsPerPacket;
        private readonly JobScheduleOptions _jobOptions;
        private JobCommandBuffer? _commands;

        internal ClassificationSnapshot(
            TClassifier classifier,
            int rowsPerPacket,
            JobScheduleOptions jobOptions)
        {
            _classifier = classifier;
            _rowsPerPacket = rowsPerPacket;
            _jobOptions = jobOptions;
            _commands = null;
        }

        internal void Record(QueryCursor cursor)
        {
            using ReadOnlyQueryPacketPlan packets =
                ReadOnlyQueryPacketJobs.CreatePlan(cursor, _rowsPerPacket);
            int rowCount = packets.RowCount;
            if (rowCount == 0)
                return;

            int[] counts = ArrayPool<int>.Shared.Rent(rowCount);
            int[] offsets = ArrayPool<int>.Shared.Rent(checked(rowCount + 1));
            TMembership[]? memberships = null;
            try
            {
                var countJob = new CountMembershipsJob(_classifier, counts);
                JobResourceAccess countWrite =
                    JobResourceAccess.Write(counts, 0, rowCount);
                int counted = packets.ExecuteParallel(
                    in countJob,
                    [countWrite],
                    _jobOptions);
                if (counted != rowCount)
                {
                    throw new InvalidOperationException(
                        "The render-classification query changed inside one read snapshot.");
                }

                var prefixJob = new PrefixMembershipCountsJob(counts, offsets, rowCount);
                JobResourceAccess[] prefixAccesses =
                [
                    JobResourceAccess.Read(counts, 0, rowCount),
                    JobResourceAccess.Write(offsets, 0, rowCount + 1),
                ];
                JobSystem.Schedule(
                    in prefixJob,
                    prefixAccesses,
                    _jobOptions).Complete();

                int membershipCount = offsets[rowCount];
                memberships = ArrayPool<TMembership>.Shared.Rent(
                    Math.Max(1, membershipCount));
                var writeJob = new WriteMembershipsJob(
                    _classifier,
                    counts,
                    offsets,
                    memberships);
                JobResourceAccess[] writeAccesses =
                [
                    JobResourceAccess.Read(counts, 0, rowCount),
                    JobResourceAccess.Read(offsets, 0, rowCount + 1),
                    membershipCount == 0
                        ? JobResourceAccess.Write(memberships)
                        : JobResourceAccess.Write(memberships, 0, membershipCount),
                ];
                int written = packets.ExecuteParallel(
                    in writeJob,
                    writeAccesses,
                    _jobOptions);
                if (written != rowCount)
                {
                    throw new InvalidOperationException(
                        "The render-classification query changed inside one read snapshot.");
                }

                var commandJob = new PublishMembershipsJob(
                    counts,
                    offsets,
                    memberships);
                JobResourceAccess[] commandAccesses =
                [
                    JobResourceAccess.Read(counts, 0, rowCount),
                    JobResourceAccess.Read(offsets, 0, rowCount + 1),
                    membershipCount == 0
                        ? JobResourceAccess.Read(memberships)
                        : JobResourceAccess.Read(memberships, 0, membershipCount),
                ];
                _commands = packets.RecordParallel(
                    in commandJob,
                    commandAccesses,
                    _jobOptions);
            }
            finally
            {
                if (memberships is not null)
                {
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<TMembership>())
                        memberships.AsSpan().Clear();
                    ArrayPool<TMembership>.Shared.Return(memberships);
                }
                ArrayPool<int>.Shared.Return(offsets);
                ArrayPool<int>.Shared.Return(counts);
            }
        }

        internal void Playback() => _commands?.Playback();

        public void Dispose()
        {
            _commands?.Dispose();
            _commands = null;
        }
    }

    private struct RemovalSnapshot : IDisposable
    {
        private readonly int _rowsPerPacket;
        private readonly JobScheduleOptions _jobOptions;
        private JobCommandBuffer? _commands;

        internal RemovalSnapshot(int rowsPerPacket, JobScheduleOptions jobOptions)
        {
            _rowsPerPacket = rowsPerPacket;
            _jobOptions = jobOptions;
            _commands = null;
        }

        internal void Record(QueryCursor cursor)
        {
            var job = new RemoveMembershipsJob();
            _commands = ReadOnlyQueryPacketJobs.RecordParallel(
                cursor,
                in job,
                ReadOnlySpan<JobResourceAccess>.Empty,
                _rowsPerPacket,
                _jobOptions);
        }

        internal void Playback() => _commands?.Playback();

        public void Dispose()
        {
            _commands?.Dispose();
            _commands = null;
        }
    }

    private readonly struct CountMembershipsJob(
        TClassifier classifier,
        int[] counts) : IReadOnlyQueryPacketJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TClassifier local = classifier;
            for (int row = 0; row < packet.Count; row++)
            {
                int count = local.Count(packet, row);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "A render classifier cannot return a negative membership count.");
                }
                counts[checked(context.OutputStart + row)] = count;
            }
        }
    }

    private readonly struct PrefixMembershipCountsJob(
        int[] counts,
        int[] offsets,
        int rowCount) : IJob
    {
        public void Execute()
        {
            int total = 0;
            offsets[0] = 0;
            for (int row = 0; row < rowCount; row++)
            {
                total = checked(total + counts[row]);
                offsets[row + 1] = total;
            }
        }
    }

    private readonly struct WriteMembershipsJob(
        TClassifier classifier,
        int[] counts,
        int[] offsets,
        TMembership[] memberships) : IReadOnlyQueryPacketJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TClassifier local = classifier;
            for (int row = 0; row < packet.Count; row++)
            {
                int output = checked(context.OutputStart + row);
                int count = counts[output];
                var writer = new RenderEntityMembershipWriter<TMembership>(
                    memberships.AsSpan(offsets[output], count));
                local.Write(packet, row, ref writer);
                writer.RequireComplete();
            }
        }
    }

    private readonly struct PublishMembershipsJob(
        int[] counts,
        int[] offsets,
        TMembership[] memberships) : IReadOnlyQueryPacketCommandJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet,
            ref JobCommandWriter commands)
        {
            bool alreadyClassified = packet.HasBuffer<TMembership>();
            for (int row = 0; row < packet.Count; row++)
            {
                int output = checked(context.OutputStart + row);
                int count = counts[output];
                if (count == 0)
                {
                    if (alreadyClassified)
                        commands.RemoveBuffer<TMembership>(packet.Entities[row]);
                    continue;
                }

                ReadOnlySpan<TMembership> values =
                    memberships.AsSpan(offsets[output], count);
                if (alreadyClassified)
                    commands.ReplaceBuffer(packet.Entities[row], values);
                else
                    commands.AddBuffer(packet.Entities[row], values);
            }
        }
    }

    private readonly struct RemoveMembershipsJob : IReadOnlyQueryPacketCommandJob
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet,
            ref JobCommandWriter commands)
        {
            for (int row = 0; row < packet.Count; row++)
                commands.RemoveBuffer<TMembership>(packet.Entities[row]);
        }
    }

}
