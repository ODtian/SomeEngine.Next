using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct BlendMeasurementPasses(
    GraphPassId[] Geometry, GraphPassId Request, GraphPassId[] Color, GraphRegionId Feedback,
    GraphPassId[] Decals, GraphPassId[] OpaqueReceivers, GraphPassId[] OpaqueColors);

internal readonly record struct BlendMeasurementBuffers(
    GraphBufferId TriangleCount, uint TriangleStride, ulong FixedBytes, ulong CapacityBytes, ulong AllocationBytes, uint TriangleCapacity);

internal readonly record struct BlendDecalBuffers(
    uint ViewCount, ulong FixedBytes, ulong PropertyBytes,
    ulong CapacityBytes, ulong AllocationBytes);

internal sealed class ClusterBlendMeasurements : IGraphReadbackReceiver, IRenderGraphFrameExecutionObserver
{
    private enum Phase { ProjectionIndex, CoverageReceiver, MaterialComposite, DecalIndex, OpaqueMaterial }
    private readonly record struct PassTime(GraphPassId Pass, Phase Phase);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private QueryPool _queries = null!;
    private GraphReadbackId _readback;
    private GraphQueryPoolId _queryId;
    private GraphBufferId _results;
    private GraphPassId _resolve;
    private PassTime[] _times = [];
    private GraphPassId[] _hardwarePasses = [];
    private readonly ulong _frequency;
    private readonly BlendMeasurementPasses _passes;
    private readonly GraphBufferId _decalWork;
    private BlendMeasurementBuffers _buffers;
    private readonly Action<ClusterBlendMetrics> _completed;
    private readonly Dictionary<ulong, MeasuredFrame> _frames = [];
    private ulong _frameTag;

    private sealed class MeasuredFrame(int simulationFrame, bool reusedIndex, bool reusedDecalIndex,
        BlendMeasurementBuffers buffers, BlendDecalBuffers decals, PassTime[] times, bool[] active)
    {
        internal readonly int SimulationFrame = simulationFrame;
        internal readonly bool ReusedIndex = reusedIndex, ReusedDecalIndex = reusedDecalIndex;
        internal readonly BlendMeasurementBuffers Buffers = buffers;
        internal readonly BlendDecalBuffers Decals = decals;
        internal readonly PassTime[] Times = times;
        internal readonly bool[] Active = active;
        internal bool Submitted;
        internal ClusterBlendMetrics? Result;
    }

    internal ClusterBlendMeasurements(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, ref RenderGraphEdit edit,
        BlendMeasurementPasses passes, BlendMeasurementBuffers buffers, GraphBufferId decalWork, Action<ClusterBlendMetrics> completed)
    {
        _backend = backend; _device = device; _graph = graph;
        _passes = passes; _buffers = buffers; _decalWork = decalWork; _completed = completed;
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        CreateResults(ref edit);
    }

    internal bool HasPasses(BlendMeasurementPasses passes) =>
        _passes.Request == passes.Request && _passes.Feedback == passes.Feedback &&
        _passes.Geometry.AsSpan().SequenceEqual(passes.Geometry) && _passes.Color.AsSpan().SequenceEqual(passes.Color) &&
        _passes.Decals.AsSpan().SequenceEqual(passes.Decals) &&
        _passes.OpaqueReceivers.AsSpan().SequenceEqual(passes.OpaqueReceivers) &&
        _passes.OpaqueColors.AsSpan().SequenceEqual(passes.OpaqueColors);

    private void CreateResults(ref RenderGraphEdit edit)
    {
        var times = new List<PassTime>();
        times.AddRange(_passes.Geometry.Select(pass => new PassTime(pass, Phase.ProjectionIndex)));
        if (_passes.Request != default) times.Add(new(_passes.Request, Phase.CoverageReceiver));
        times.AddRange(_passes.Color.Concat(_hardwarePasses).Select(pass => new PassTime(pass, Phase.MaterialComposite)));
        times.AddRange(_passes.Decals.Select(pass => new PassTime(pass, Phase.DecalIndex)));
        times.AddRange(_passes.OpaqueReceivers.Concat(_passes.OpaqueColors).Select(pass => new PassTime(pass, Phase.OpaqueMaterial)));
        _times = times.Distinct().ToArray();
        uint queryCount = (uint)_times.Length * 2u;
        _queries = _backend.CreateQueryPool(_device, new(QueryType.Timestamp, QueueType.Graphics, queryCount,
            Label: "Cluster blend pass times"));
        _queryId = edit.RegisterQueryPool(_queries);
        for (int index = 0; index < _times.Length; index++)
            edit.MeasurePassTime(_times[index].Pass, _queryId, (uint)index * 2u);
        ulong byteCount = (ulong)_times.Length * 16u + 32u;
        _results = edit.CreateBuffer(new BufferDesc(byteCount,
            BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Cluster blend timing and work results"));
        _resolve = edit.AddGeneralFramePass("Resolve Cluster blend measurements",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Results(_queryId, _results, _buffers.TriangleCount, _decalWork, []),
            new PassOptions(Culling: PassCullingMode.NeverCull), Results.Declare, Results.Record,
            region: _passes.Feedback);
        _readback = edit.AddReadback(_results, BufferRange.Whole, this,
            "Observe Cluster blend measurements", _passes.Feedback);
        edit.OrderAfter(_readback, _resolve);
    }

    internal void Stage(ref RenderGraphFrame graph, int simulationFrame, bool reusedIndex, bool reusedDecalIndex,
        BlendDecalBuffers decals)
    {
        var active = new bool[_times.Length];
        _frameTag = graph.FrameIdentity;
        _frames.Add(_frameTag, new(simulationFrame, reusedIndex, reusedDecalIndex, _buffers, decals, _times, active));
        graph.SetReadbackTag(_readback, _frameTag);
        graph.ObserveExecution(this);
    }

    public void OnRenderGraphFramePreparing(ref RenderGraphFrame graph)
    {
        MeasuredFrame frame = _frames[graph.FrameIdentity];
        PassDefinition resolve = graph.GetPass(_resolve);
        for (int index = 0; index < frame.Times.Length; index++)
        {
            frame.Active[index] = graph.IsPassEnabled(frame.Times[index].Pass);
            if (frame.Active[index]) resolve.Read(_queryId, new QueryRange((uint)index * 2u, 2));
        }
        graph.SetPassData(_resolve, new Results(_queryId, _results, _buffers.TriangleCount, _decalWork, frame.Active));
    }

    internal void Remove(ref RenderGraphEdit edit)
    {
        edit.Remove(_readback); edit.Remove(_resolve); edit.Remove(_results); edit.Remove(_queryId);
        _graph.RetireAfterSubmittedFrames(_queries);
    }

    public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!_frames.TryGetValue(_frameTag, out MeasuredFrame? frame)) return;
        if (!result.TryGetReadbackCompletion(_readback, out _))
        {
            _frames.Remove(_frameTag);
            return;
        }
        frame.Submitted = true;
        Accept(_frameTag, frame);
    }

    public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
    {
        if (!_frames.TryGetValue(tag, out MeasuredFrame? frame)) return;
        int timeBytes = frame.Times.Length * 16;
        ReadOnlySpan<ulong> times = MemoryMarshal.Cast<byte, ulong>(bytes[..timeBytes]);
        Span<double> milliseconds = stackalloc double[5];
        milliseconds.Clear();
        for (int index = 0; index < frame.Times.Length; index++)
            if (frame.Active[index])
                milliseconds[(int)frame.Times[index].Phase] +=
                    1000.0 * (times[index * 2 + 1] - times[index * 2]) / _frequency;
        ReadOnlySpan<byte> work = bytes[timeBytes..];
        uint triangles = BinaryPrimitives.ReadUInt32LittleEndian(work);
        uint references = BinaryPrimitives.ReadUInt32LittleEndian(work[4..]);
        uint residentReferences = BinaryPrimitives.ReadUInt32LittleEndian(work[8..]);
        uint sources = BinaryPrimitives.ReadUInt32LittleEndian(work[12..]);
        uint decalReferences = BinaryPrimitives.ReadUInt32LittleEndian(work[16..]);
        uint decalResidentReferences = BinaryPrimitives.ReadUInt32LittleEndian(work[20..]);
        uint decalVolumes = BinaryPrimitives.ReadUInt32LittleEndian(work[24..]);
        BlendMeasurementBuffers buffers = frame.Buffers;
        BlendDecalBuffers decals = frame.Decals;
        ulong decalUsed = decals.FixedBytes + (ulong)decalVolumes * (144u + decals.ViewCount * 32u) +
            (decalVolumes == 0 ? 0 : decals.PropertyBytes) + (ulong)decalResidentReferences * sizeof(uint);
        ulong used = buffers.FixedBytes + (ulong)Math.Min(triangles, buffers.TriangleCapacity) * buffers.TriangleStride +
            (ulong)residentReferences * sizeof(uint) + (ulong)sources * 12u;
        frame.Result = new(frame.SimulationFrame, tag,
            milliseconds[(int)Phase.ProjectionIndex], milliseconds[(int)Phase.CoverageReceiver], milliseconds[(int)Phase.MaterialComposite],
            triangles, references, used + decalUsed, buffers.CapacityBytes + decals.CapacityBytes,
            buffers.AllocationBytes + decals.AllocationBytes, frame.ReusedIndex,
            milliseconds[(int)Phase.DecalIndex], milliseconds[(int)Phase.OpaqueMaterial],
            decalVolumes, decalReferences, decalResidentReferences,
            decalUsed, decals.CapacityBytes, decals.AllocationBytes, frame.ReusedDecalIndex);
        Accept(tag, frame);
    }

    private void Accept(ulong tag, MeasuredFrame frame)
    {
        if (!frame.Submitted || frame.Result is not { } result) return;
        _frames.Remove(tag);
        _completed(result);
    }

    internal void IncludeColorPasses(ref RenderGraphEdit edit, GraphPassId[] passes, ulong capacityBytes, ulong allocationBytes)
    {
        _hardwarePasses = passes;
        _buffers = _buffers with { FixedBytes = _buffers.FixedBytes + capacityBytes,
            CapacityBytes = _buffers.CapacityBytes + capacityBytes, AllocationBytes = _buffers.AllocationBytes + allocationBytes };
        Remove(ref edit);
        CreateResults(ref edit);
    }

    private readonly record struct Results(GraphQueryPoolId Queries, GraphBufferId Destination,
        GraphBufferId TriangleCount, GraphBufferId DecalWork, bool[] Active)
    {
        internal static void Declare(ref PassDefinition pass, ref Results data)
        {
            if (data.TriangleCount != default)
                _ = pass.Read(data.TriangleCount, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Read(data.DecalWork, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref GeneralPassCommandScope commands, in Results data)
        {
            var destination = commands.GetBuffer(data.Destination);
            for (int index = 0; index < data.Active.Length; index++)
            {
                var range = new BufferRange((ulong)index * 16u, 16);
                if (data.Active[index]) commands.ResolveQueries(data.Queries, (uint)index * 2u, 2, destination, range);
                else commands.ClearBuffer(destination, range);
            }
            ulong timeBytes = (ulong)data.Active.Length * 16u;
            if (data.TriangleCount == default) commands.ClearBuffer(destination, new BufferRange(timeBytes, 16));
            else commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.TriangleCount), 0, destination, timeBytes, 16));
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.DecalWork), 0, destination, timeBytes + 16, 16));
        }
    }
}
