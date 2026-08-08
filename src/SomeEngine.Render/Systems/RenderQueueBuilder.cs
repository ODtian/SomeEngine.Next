using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Render.Systems;

/// <summary>Exact visible-work counts produced for one entity in one view.</summary>
public readonly struct RenderQueueWorkCounts
{
    public RenderQueueWorkCounts(int stateGrouped, int backToFront)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stateGrouped);
        ArgumentOutOfRangeException.ThrowIfNegative(backToFront);
        StateGrouped = stateGrouped;
        BackToFront = backToFront;
    }

    public int StateGrouped { get; }

    public int BackToFront { get; }
}

/// <summary>
/// Current-query coordinate for one RenderWorld entity. It lets a concrete pipeline correlate
/// its immutable instance-batch plan without introducing a persistent instance slot or ID.
/// </summary>
public readonly record struct RenderQueueEntityContext(int QueryIndex);

/// <summary>
/// One contiguous run of draw payloads sharing the concrete pipeline-defined state key. Ordered
/// queues may contain several runs with the same key because depth order takes precedence.
/// </summary>
public readonly record struct RenderQueueBin<TBinKey>(
    TBinKey Key,
    int Start,
    int Count);

/// <summary>
/// Callback-scoped immutable queue output. State-grouped work is gathered by key. Back-to-front
/// work remains globally depth ordered and is only coalesced when adjacent keys are equal.
/// </summary>
public readonly ref struct RenderQueueView<TBinKey, TDraw>
{
    internal RenderQueueView(
        ReadOnlySpan<RenderQueueBin<TBinKey>> stateBins,
        ReadOnlySpan<TDraw> stateDraws,
        ReadOnlySpan<RenderQueueBin<TBinKey>> backToFrontBins,
        ReadOnlySpan<TDraw> backToFrontDraws)
    {
        StateBins = stateBins;
        StateDraws = stateDraws;
        BackToFrontBins = backToFrontBins;
        BackToFrontDraws = backToFrontDraws;
    }

    public ReadOnlySpan<RenderQueueBin<TBinKey>> StateBins { get; }

    public ReadOnlySpan<TDraw> StateDraws { get; }

    public ReadOnlySpan<RenderQueueBin<TBinKey>> BackToFrontBins { get; }

    public ReadOnlySpan<TDraw> BackToFrontDraws { get; }
}

public delegate void RenderQueueExecution<TState, TBinKey, TDraw>(
    ref TState state,
    RenderQueueView<TBinKey, TDraw> queue)
    where TState : allows ref struct;

/// <summary>
/// Concrete-pipeline visibility and queue emission contract. The pipeline interprets its own
/// membership elements and defines both the true state key and draw payload. Count and Write run
/// in separate parallel passes over one immutable RenderWorld snapshot.
/// </summary>
public interface IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
    where TMembership : struct, IBufferElement
    where TBinKey : notnull
{
    RenderQueueWorkCounts Count(
        in TView view,
        in RenderQueueEntityContext entity,
        ReadOnlyQueryPacket packet,
        int row,
        BufferView<TMembership> memberships);

    void Write(
        in TView view,
        in RenderQueueEntityContext entity,
        ReadOnlyQueryPacket packet,
        int row,
        BufferView<TMembership> memberships,
        ref RenderQueueWorkWriter<TBinKey, TDraw> output);
}

/// <summary>
/// Exact, entity-local visible-work destination. A pipeline chooses state grouping or strict
/// back-to-front ordering per emitted pass; the infrastructure assigns no pass, material, PSO,
/// layout, or signature identity of its own.
/// </summary>
public ref struct RenderQueueWorkWriter<TBinKey, TDraw>
    where TBinKey : notnull
{
    private readonly Span<TBinKey> _stateKeys;
    private readonly Span<TDraw> _stateDraws;
    private readonly Span<OrderedRenderWork<TBinKey, TDraw>> _ordered;
    private readonly int _orderedSequenceStart;
    private int _stateCount;
    private int _orderedCount;

    internal RenderQueueWorkWriter(
        Span<TBinKey> stateKeys,
        Span<TDraw> stateDraws,
        Span<OrderedRenderWork<TBinKey, TDraw>> ordered,
        int orderedSequenceStart)
    {
        _stateKeys = stateKeys;
        _stateDraws = stateDraws;
        _ordered = ordered;
        _orderedSequenceStart = orderedSequenceStart;
        _stateCount = 0;
        _orderedCount = 0;
    }

    public int StateCapacity => _stateKeys.Length;

    public int StateCount => _stateCount;

    public int BackToFrontCapacity => _ordered.Length;

    public int BackToFrontCount => _orderedCount;

    public void AddStateGrouped(in TBinKey key, in TDraw draw)
    {
        if ((uint)_stateCount >= (uint)_stateKeys.Length)
            throw new InvalidOperationException("The render queue wrote more state-grouped work than it counted.");

        _stateKeys[_stateCount] = key;
        _stateDraws[_stateCount] = draw;
        _stateCount++;
    }

    public void AddBackToFront(float depth, in TBinKey key, in TDraw draw)
    {
        if (float.IsNaN(depth))
            throw new ArgumentOutOfRangeException(nameof(depth), "Render queue depth cannot be NaN.");
        if ((uint)_orderedCount >= (uint)_ordered.Length)
            throw new InvalidOperationException("The render queue wrote more ordered work than it counted.");

        _ordered[_orderedCount] = new OrderedRenderWork<TBinKey, TDraw>(
            key,
            draw,
            depth,
            checked(_orderedSequenceStart + _orderedCount));
        _orderedCount++;
    }

    internal void RequireComplete()
    {
        if (_stateCount != _stateKeys.Length || _orderedCount != _ordered.Length)
        {
            throw new InvalidOperationException(
                $"The render queue counted ({_stateKeys.Length}, {_ordered.Length}) work items " +
                $"but wrote ({_stateCount}, {_orderedCount}).");
        }
    }
}

/// <summary>
/// Pipeline-owned per-view queue builder. It packetizes the pipeline's query once, then reuses that
/// plan for count and fill; it never scans once per bin. The caller supplies the fixed
/// state-grouping partition count from measured policy, and this class contains no hidden
/// direct-submit or binning threshold.
/// </summary>
public sealed class RenderQueueBuilder<TMembership, TBinKey, TDraw>
    where TMembership : struct, IBufferElement
    where TBinKey : notnull
{
    private readonly StableGrouping<TBinKey> _grouping;
    private readonly int _rowsPerPacket;
    private readonly JobScheduleOptions _jobOptions;
    private int _building;

    public RenderQueueBuilder(
        int stateGroupingPartitions,
        int rowsPerPacket = 0,
        JobScheduleOptions jobOptions = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stateGroupingPartitions);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsPerPacket);
        _grouping = new StableGrouping<TBinKey>(stateGroupingPartitions);
        _rowsPerPacket = rowsPerPacket;
        _jobOptions = jobOptions;
    }

    public void Build<TView, TClassifier, TState>(
        RenderWorld world,
        QueryHandle query,
        in TView view,
        in TClassifier classifier,
        scoped ref TState state,
        RenderQueueExecution<TState, TBinKey, TDraw> execution)
        where TClassifier : struct,
            IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(execution);
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
            throw new InvalidOperationException("A render queue builder cannot build two views concurrently.");

        try
        {
            var snapshot = new QueueSnapshot<TView, TClassifier, TState>(
                this,
                view,
                classifier,
                state,
                execution);
            world.ExecuteReadSnapshot(
                query,
                ref snapshot,
                static (QueryCursor cursor, ref QueueSnapshot<TView, TClassifier, TState> snapshot) =>
                    snapshot.Build(cursor));
            state = snapshot.State;
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }
    }

    private void BuildGrouped(
        TBinKey[] sourceKeys,
        TDraw[] sourceDraws,
        int workCount,
        RenderQueueBin<TBinKey>[] bins,
        TDraw[] draws,
        out int binCount)
    {
        if (workCount == 0)
        {
            binCount = 0;
            return;
        }

        int[] groupCounts = ArrayPool<int>.Shared.Rent(workCount);
        int[] groupStarts = ArrayPool<int>.Shared.Rent(workCount);
        StableGroupPlacement[] placements =
            ArrayPool<StableGroupPlacement>.Shared.Rent(workCount);
        try
        {
            binCount = _grouping.Group(
                sourceKeys,
                workCount,
                groupCounts,
                groupStarts,
                placements,
                _jobOptions);
            for (int group = 0; group < binCount; group++)
            {
                bins[group] = new RenderQueueBin<TBinKey>(
                    _grouping.GetKey(group),
                    groupStarts[group],
                    groupCounts[group]);
            }

            var scatterJob = new ScatterGroupedDrawsJob(
                sourceDraws,
                placements,
                groupStarts,
                draws);
            JobResourceAccess[] scatterAccesses =
            [
                JobResourceAccess.Read(sourceDraws, 0, workCount),
                JobResourceAccess.Read(placements, 0, workCount),
                JobResourceAccess.Read(groupStarts, 0, binCount),
                JobResourceAccess.Write(draws, 0, workCount),
            ];
            JobSystem.ScheduleParallel(
                in scatterJob,
                workCount,
                batchSize: 1,
                scatterAccesses,
                _jobOptions).Complete();
        }
        finally
        {
            ArrayPool<StableGroupPlacement>.Shared.Return(placements);
            ArrayPool<int>.Shared.Return(groupStarts);
            ArrayPool<int>.Shared.Return(groupCounts);
        }
    }

    private void BuildBackToFront(
        OrderedRenderWork<TBinKey, TDraw>[] source,
        int workCount,
        RenderQueueBin<TBinKey>[] bins,
        TDraw[] draws,
        out int binCount)
    {
        if (workCount == 0)
        {
            binCount = 0;
            return;
        }

        int[] binCountResult = ArrayPool<int>.Shared.Rent(1);
        try
        {
            var job = new OrderBackToFrontJob(
                source,
                workCount,
                bins,
                draws,
                binCountResult);
            JobResourceAccess[] accesses =
            [
                JobResourceAccess.Write(source, 0, workCount),
                JobResourceAccess.Write(bins, 0, workCount),
                JobResourceAccess.Write(draws, 0, workCount),
                JobResourceAccess.Write(binCountResult, 0, 1),
            ];
            JobSystem.Schedule(in job, accesses, _jobOptions).Complete();
            binCount = binCountResult[0];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(binCountResult);
        }
    }

    private ref struct QueueSnapshot<TView, TClassifier, TState>
        where TClassifier : struct,
            IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
        where TState : allows ref struct
    {
        private readonly RenderQueueBuilder<TMembership, TBinKey, TDraw> _owner;
        private readonly TView _view;
        private readonly TClassifier _classifier;
        private readonly RenderQueueExecution<TState, TBinKey, TDraw> _execution;
        private TState _state;

        internal QueueSnapshot(
            RenderQueueBuilder<TMembership, TBinKey, TDraw> owner,
            TView view,
            TClassifier classifier,
            TState state,
            RenderQueueExecution<TState, TBinKey, TDraw> execution)
        {
            _owner = owner;
            _view = view;
            _classifier = classifier;
            _state = state;
            _execution = execution;
        }

        internal TState State => _state;

        internal void Build(QueryCursor cursor)
        {
            using ReadOnlyQueryPacketPlan packets =
                ReadOnlyQueryPacketJobs.CreatePlan(cursor, _owner._rowsPerPacket);
            int rowCount = packets.RowCount;
            if (rowCount == 0)
            {
                _execution(ref _state, new RenderQueueView<TBinKey, TDraw>(
                    ReadOnlySpan<RenderQueueBin<TBinKey>>.Empty,
                    ReadOnlySpan<TDraw>.Empty,
                    ReadOnlySpan<RenderQueueBin<TBinKey>>.Empty,
                    ReadOnlySpan<TDraw>.Empty));
                return;
            }

            RenderQueueWorkCounts[] counts =
                ArrayPool<RenderQueueWorkCounts>.Shared.Rent(rowCount);
            int[] stateOffsets = ArrayPool<int>.Shared.Rent(checked(rowCount + 1));
            int[] orderedOffsets = ArrayPool<int>.Shared.Rent(checked(rowCount + 1));
            TBinKey[]? stateKeys = null;
            TDraw[]? stateSourceDraws = null;
            OrderedRenderWork<TBinKey, TDraw>[]? ordered = null;
            RenderQueueBin<TBinKey>[]? stateBins = null;
            RenderQueueBin<TBinKey>[]? orderedBins = null;
            TDraw[]? stateDraws = null;
            TDraw[]? orderedDraws = null;
            try
            {
                var countJob = new CountQueueWorkJob<TView, TClassifier>(
                    _view,
                    _classifier,
                    counts);
                JobResourceAccess countAccess =
                    JobResourceAccess.Write(counts, 0, rowCount);
                int counted = packets.ExecuteParallel(
                    in countJob,
                    [countAccess],
                    _owner._jobOptions);
                if (counted != rowCount)
                    throw new InvalidOperationException("The render queue query changed inside one read snapshot.");

                var prefixJob = new PrefixQueueWorkJob(
                    counts,
                    stateOffsets,
                    orderedOffsets,
                    rowCount);
                JobResourceAccess[] prefixAccesses =
                [
                    JobResourceAccess.Read(counts, 0, rowCount),
                    JobResourceAccess.Write(stateOffsets, 0, rowCount + 1),
                    JobResourceAccess.Write(orderedOffsets, 0, rowCount + 1),
                ];
                JobSystem.Schedule(
                    in prefixJob,
                    prefixAccesses,
                    _owner._jobOptions).Complete();

                int stateCount = stateOffsets[rowCount];
                int orderedCount = orderedOffsets[rowCount];
                stateKeys = ArrayPool<TBinKey>.Shared.Rent(Math.Max(1, stateCount));
                stateSourceDraws = ArrayPool<TDraw>.Shared.Rent(Math.Max(1, stateCount));
                ordered = ArrayPool<OrderedRenderWork<TBinKey, TDraw>>.Shared.Rent(
                    Math.Max(1, orderedCount));
                stateBins = ArrayPool<RenderQueueBin<TBinKey>>.Shared.Rent(
                    Math.Max(1, stateCount));
                orderedBins = ArrayPool<RenderQueueBin<TBinKey>>.Shared.Rent(
                    Math.Max(1, orderedCount));
                stateDraws = ArrayPool<TDraw>.Shared.Rent(Math.Max(1, stateCount));
                orderedDraws = ArrayPool<TDraw>.Shared.Rent(Math.Max(1, orderedCount));

                var writeJob = new WriteQueueWorkJob<TView, TClassifier>(
                    _view,
                    _classifier,
                    counts,
                    stateOffsets,
                    orderedOffsets,
                    stateKeys,
                    stateSourceDraws,
                    ordered);
                JobResourceAccess[] writeAccesses =
                [
                    JobResourceAccess.Read(counts, 0, rowCount),
                    JobResourceAccess.Read(stateOffsets, 0, rowCount + 1),
                    JobResourceAccess.Read(orderedOffsets, 0, rowCount + 1),
                    RangeWrite(stateKeys, stateCount),
                    RangeWrite(stateSourceDraws, stateCount),
                    RangeWrite(ordered, orderedCount),
                ];
                int written = packets.ExecuteParallel(
                    in writeJob,
                    writeAccesses,
                    _owner._jobOptions);
                if (written != rowCount)
                    throw new InvalidOperationException("The render queue query changed inside one read snapshot.");

                _owner.BuildGrouped(
                    stateKeys,
                    stateSourceDraws,
                    stateCount,
                    stateBins,
                    stateDraws,
                    out int stateBinCount);
                _owner.BuildBackToFront(
                    ordered,
                    orderedCount,
                    orderedBins,
                    orderedDraws,
                    out int orderedBinCount);

                _execution(ref _state, new RenderQueueView<TBinKey, TDraw>(
                    stateBins.AsSpan(0, stateBinCount),
                    stateDraws.AsSpan(0, stateCount),
                    orderedBins.AsSpan(0, orderedBinCount),
                    orderedDraws.AsSpan(0, orderedCount)));
            }
            finally
            {
                Return(stateDraws);
                Return(orderedDraws);
                Return(stateBins);
                Return(orderedBins);
                Return(stateKeys);
                Return(stateSourceDraws);
                Return(ordered);
                ArrayPool<int>.Shared.Return(orderedOffsets);
                ArrayPool<int>.Shared.Return(stateOffsets);
                ArrayPool<RenderQueueWorkCounts>.Shared.Return(counts);
            }
        }

        private static JobResourceAccess RangeWrite<T>(T[] array, int count) =>
            count == 0
                ? JobResourceAccess.Write(array)
                : JobResourceAccess.Write(array, 0, count);

        private static void Return<T>(T[]? array)
        {
            if (array is null)
                return;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                array.AsSpan().Clear();
            ArrayPool<T>.Shared.Return(array);
        }
    }

    private readonly struct CountQueueWorkJob<TView, TClassifier>(
        TView view,
        TClassifier classifier,
        RenderQueueWorkCounts[] counts) : IReadOnlyQueryPacketJob
        where TClassifier : struct,
            IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TClassifier local = classifier;
            for (int row = 0; row < packet.Count; row++)
            {
                int output = checked(context.OutputStart + row);
                var entity = new RenderQueueEntityContext(output);
                counts[output] = local.Count(
                    in view,
                    in entity,
                    packet,
                    row,
                    packet.ReadBuffer<TMembership>(row));
            }
        }
    }

    private readonly struct PrefixQueueWorkJob(
        RenderQueueWorkCounts[] counts,
        int[] stateOffsets,
        int[] orderedOffsets,
        int rowCount) : IJob
    {
        public void Execute()
        {
            int state = 0;
            int ordered = 0;
            stateOffsets[0] = 0;
            orderedOffsets[0] = 0;
            for (int row = 0; row < rowCount; row++)
            {
                state = checked(state + counts[row].StateGrouped);
                ordered = checked(ordered + counts[row].BackToFront);
                stateOffsets[row + 1] = state;
                orderedOffsets[row + 1] = ordered;
            }
        }
    }

    private readonly struct WriteQueueWorkJob<TView, TClassifier>(
        TView view,
        TClassifier classifier,
        RenderQueueWorkCounts[] counts,
        int[] stateOffsets,
        int[] orderedOffsets,
        TBinKey[] stateKeys,
        TDraw[] stateDraws,
        OrderedRenderWork<TBinKey, TDraw>[] ordered) : IReadOnlyQueryPacketJob
        where TClassifier : struct,
            IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
    {
        public void Execute(
            in ReadOnlyQueryPacketContext context,
            ReadOnlyQueryPacket packet)
        {
            TClassifier local = classifier;
            for (int row = 0; row < packet.Count; row++)
            {
                int output = checked(context.OutputStart + row);
                RenderQueueWorkCounts rowCounts = counts[output];
                int stateStart = stateOffsets[output];
                int orderedStart = orderedOffsets[output];
                var writer = new RenderQueueWorkWriter<TBinKey, TDraw>(
                    stateKeys.AsSpan(stateStart, rowCounts.StateGrouped),
                    stateDraws.AsSpan(stateStart, rowCounts.StateGrouped),
                    ordered.AsSpan(orderedStart, rowCounts.BackToFront),
                    orderedStart);
                var entity = new RenderQueueEntityContext(output);
                local.Write(
                    in view,
                    in entity,
                    packet,
                    row,
                    packet.ReadBuffer<TMembership>(row),
                    ref writer);
                writer.RequireComplete();
            }
        }
    }

    private readonly struct ScatterGroupedDrawsJob(
        TDraw[] source,
        StableGroupPlacement[] placements,
        int[] groupStarts,
        TDraw[] destination) : IJobParallelFor
    {
        public void Execute(int index)
        {
            StableGroupPlacement placement = placements[index];
            destination[checked(groupStarts[placement.Group] + placement.Row)] = source[index];
        }
    }

    private readonly struct OrderBackToFrontJob(
        OrderedRenderWork<TBinKey, TDraw>[] source,
        int workCount,
        RenderQueueBin<TBinKey>[] bins,
        TDraw[] draws,
        int[] binCountResult) : IJob
    {
        public void Execute()
        {
            Array.Sort(
                source,
                0,
                workCount,
                OrderedRenderWorkComparer<TBinKey, TDraw>.Instance);

            int binCount = 0;
            int runStart = 0;
            for (int work = 0; work < workCount; work++)
            {
                OrderedRenderWork<TBinKey, TDraw> current = source[work];
                draws[work] = current.Draw;
                if (work == 0 || EqualityComparer<TBinKey>.Default.Equals(
                        source[work - 1].Key,
                        current.Key))
                {
                    continue;
                }

                OrderedRenderWork<TBinKey, TDraw> previous = source[work - 1];
                bins[binCount++] = new RenderQueueBin<TBinKey>(
                    previous.Key,
                    runStart,
                    work - runStart);
                runStart = work;
            }

            OrderedRenderWork<TBinKey, TDraw> last = source[workCount - 1];
            bins[binCount++] = new RenderQueueBin<TBinKey>(
                last.Key,
                runStart,
                workCount - runStart);
            binCountResult[0] = binCount;
        }
    }
}

internal readonly record struct OrderedRenderWork<TBinKey, TDraw>(
    TBinKey Key,
    TDraw Draw,
    float Depth,
    int Sequence)
    where TBinKey : notnull;

internal sealed class OrderedRenderWorkComparer<TBinKey, TDraw> :
    IComparer<OrderedRenderWork<TBinKey, TDraw>>
    where TBinKey : notnull
{
    internal static OrderedRenderWorkComparer<TBinKey, TDraw> Instance { get; } = new();

    public int Compare(
        OrderedRenderWork<TBinKey, TDraw> left,
        OrderedRenderWork<TBinKey, TDraw> right)
    {
        int depth = right.Depth.CompareTo(left.Depth);
        return depth != 0 ? depth : left.Sequence.CompareTo(right.Sequence);
    }
}
