namespace SomeEngine.RenderGraph;

/// <summary>GPU command time and barrier-inclusive boundaries, relative to the frame's first GPU work.</summary>
public readonly record struct GpuPassTime(
    GraphPassId Pass, string Label, string? Group, QueueType QueueType, uint QueueIndex,
    double StartMs, double EndMs, double CommandsMs);

/// <summary>Completed GPU work across all actual queues, expressed on the calibrated CPU clock.</summary>
public sealed record RenderGraphGpuTimes(ulong FrameIdentity, double ElapsedMs, IReadOnlyList<GpuPassTime> Passes);

internal sealed class FrameGpuTimes(RenderGraph graph) : IDisposable
{
    private readonly Dictionary<Queue, QueueTimeQueries> _queues = [];
    private readonly List<TimedPass> _passes = [];
    private Action<RenderGraphGpuTimes>? _receive;
    private ulong _identity;

    internal void Begin(ulong identity, Action<RenderGraphGpuTimes> receive)
    {
        _identity = identity;
        _receive = receive;
        _passes.Clear();
        foreach (QueueTimeQueries queue in _queues.Values) queue.Count = 0;
    }

    internal PassTimeQueries Add(GraphPassId pass, string label, string? group, Queue queue)
    {
        if (!_queues.TryGetValue(queue, out QueueTimeQueries? queries))
            _queues.Add(queue, queries = new QueueTimeQueries(graph, queue));
        var range = new PassTimeQueries(queries, queries.Count);
        queries.Count += 4;
        _passes.Add(new(pass, label, group, range));
        return range;
    }

    internal void Prepare()
    {
        foreach (QueueTimeQueries queue in _queues.Values)
            if (queue.Count > 0) queue.Prepare();
    }

    internal void Resolve(Action<QueueCompletion> submitted)
    {
        foreach (QueueTimeQueries queue in _queues.Values)
        {
            if (queue.Count == 0) continue;
            using CommandContextPool.CommandContextLease lease =
                graph.CommandContexts.Acquire(queue.Queue, false, "GPU time readback");
            CommandContext context = lease.Context;
            graph.Backend.Begin(context);
            graph.Backend.ResolveQueries(context, queue.Pool!, 0, queue.Count,
                queue.Readback!, new(0, queue.Count * queue.Pool!.ResultInfo.ResultStride));
            using RecordedCommands commands = graph.Backend.End(context);
            RecordedCommands[] batch = [commands];
            // The same queue's FIFO orders every application timestamp before its resolve.
            submitted(graph.Backend.Submit(queue.Queue, new QueueSubmitDesc([], [], batch, [], [])));
        }
    }

    // Called by the frame slot after every accepted submission, including resolves, completes.
    internal void Collect()
    {
        Action<RenderGraphGpuTimes>? receive = _receive;
        _receive = null;
        if (receive is null) return;
        foreach (QueueTimeQueries queue in _queues.Values)
            if (queue.Count > 0) queue.Read();
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        foreach (TimedPass pass in _passes)
        {
            first = Math.Min(first, pass.Queries.Time(0));
            last = Math.Max(last, pass.Queries.Time(3));
        }
        var times = new GpuPassTime[_passes.Count];
        for (int index = 0; index < times.Length; index++)
        {
            TimedPass pass = _passes[index];
            PassTimeQueries queries = pass.Queries;
            times[index] = new(pass.Pass, pass.Label, pass.Group, queries.Queue.Queue.Type, queries.Queue.Queue.Index,
                (queries.Time(0) - first) * 1000, (queries.Time(3) - first) * 1000,
                (queries.Queue.Values[queries.First + 2] - queries.Queue.Values[queries.First + 1])
                    * 1000.0 / queries.Queue.Calibration.QueueFrequency);
        }
        receive(new(_identity, times.Length == 0 ? 0 : (last - first) * 1000, times));
    }

    internal void Cancel() => _receive = null;

    public void Dispose()
    {
        foreach (QueueTimeQueries queue in _queues.Values) queue.Dispose();
        _queues.Clear();
    }

    private readonly record struct TimedPass(GraphPassId Pass, string Label, string? Group, PassTimeQueries Queries);

    internal sealed class QueueTimeQueries(RenderGraph graph, Queue queue) : IDisposable
    {
        internal Queue Queue => queue;
        internal uint Count;
        internal QueryPool? Pool;
        internal Buffer? Readback;
        internal ulong[] Values = [];
        internal CalibratedTimestampInfo Calibration;

        internal void Prepare()
        {
            if (Pool is null || Count > Pool.Description.Count)
            {
                Dispose();
                uint capacity = BitOperations.RoundUpToPowerOf2(Count);
                Pool = graph.Backend.CreateQueryPool(graph.Device,
                    new(QueryType.Timestamp, queue.Type, capacity, Label: "GPU frame times"));
                Readback = graph.Backend.CreateBuffer(graph.Device,
                    new(capacity * Pool.ResultInfo.ResultStride, BufferUsages.QueryResolve, "GPU time readback"),
                    MemoryType.Readback);
                Values = new ulong[capacity];
            }
            Calibration = graph.Backend.CalibrateTimestamps(queue);
        }

        internal void Read()
        {
            BufferRange range = new(0, Count * Pool!.ResultInfo.ResultStride);
            using MappedBuffer mapped = graph.Backend.Map(Readback!, MapType.Read, range);
            mapped.Invalidate(range);
            ReadOnlySpan<byte> bytes = mapped.Bytes;
            for (uint index = 0; index < Count; index++)
                Values[index] = MemoryMarshal.Read<ulong>(bytes.Slice((int)(index * Pool.ResultInfo.ResultStride)));
        }

        public void Dispose()
        {
            Readback?.Dispose();
            Pool?.Dispose();
            Readback = null;
            Pool = null;
        }
    }

    internal readonly record struct PassTimeQueries(QueueTimeQueries Queue, uint First)
    {
        internal double Time(uint offset)
        {
            CalibratedTimestampInfo calibration = Queue.Calibration;
            double ticks = (double)Queue.Values[First + offset] - calibration.QueueCounter;
            return (double)calibration.CpuCounter / calibration.CpuFrequency + ticks / calibration.QueueFrequency;
        }
    }
}

internal sealed partial class FrameExecutor
{
    private FrameGpuTimes? _gpuTimes;
    private FrameGpuTimes.PassTimeQueries[] _passTimes = [];
    private readonly Dictionary<Queue, FrameGpuTimes.PassTimeQueries> _releaseTimes = [];
    private bool _measureGpu;

    private void PrepareGpuTimes()
    {
        bool enabled = _frame.Options.GpuTimes is not null;
        if (_measureGpu != enabled)
            foreach (int pass in _executionOrder.Nodes) ChangeRecording(pass);
        _measureGpu = enabled;
        if (!enabled) return;
        _gpuTimes ??= new FrameGpuTimes(_frame.Graph);
        _gpuTimes.Begin(_frame.Identity, _frame.Options.GpuTimes!);
        EnsureCapacity(ref _passTimes, _passes.Count);
        foreach (int pass in _executionOrder.Nodes)
        {
            FramePass row = _passes[pass];
            _passTimes[pass] = _gpuTimes.Add(new(row.Identity), row.Label, row.Options.TimeGroup, row.Queue!);
        }
        _releaseTimes.Clear();
        foreach (PendingQueueRelease release in _lateReleases)
            if (!_releaseTimes.ContainsKey(release.SourceQueue))
                _releaseTimes.Add(release.SourceQueue,
                    _gpuTimes.Add(default, "Queue resource transfers", null, release.SourceQueue));
        _gpuTimes.Prepare();
    }

    private void WriteGpuTime(CommandContext context, in FrameGpuTimes.PassTimeQueries queries, uint offset)
    {
        if (offset == 0) _frame.Backend.ResetQueries(context, queries.Queue.Pool!, queries.First, 4);
        _frame.Backend.WriteTimestamp(context, queries.Queue.Pool!, queries.First + offset);
    }

    private void ResolveGpuTimes(Span<QueueCompletion> destination)
    {
        if (!_measureGpu) return;
        // Spans stay in the caller; capture accepted resolves in the existing completion list.
        _gpuTimes!.Resolve(completion => AddCompletion(_submittedCompletions, completion));
        foreach (QueueCompletion completion in _submittedCompletions)
        {
            int index = QueueSlot(completion.Queue);
            if (index < 0) continue;
            destination[index] = completion;
            _submittedQueues[index] = true;
        }
    }

    internal void CollectGpuTimes() => _gpuTimes?.Collect();
    internal void DisposeGpuTimes() => _gpuTimes?.Dispose();
}
