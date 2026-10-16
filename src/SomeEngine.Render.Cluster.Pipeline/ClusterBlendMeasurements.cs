using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct BlendMeasurementPasses(
    GraphRegionId Geometry, GraphPassId GeometryFirst, GraphPassId GeometryLast,
    GraphRegionId Requests, GraphPassId Request,
    GraphRegionId Color, GraphPassId ColorFirst, GraphPassId Resolve, GraphRegionId ColorEnd, GraphRegionId Feedback,
    GraphRegionId Decals, GraphPassId DecalFirst, GraphPassId DecalLast,
    GraphRegionId OpaqueReceivers, GraphRegionId OpaqueColors);

internal readonly record struct BlendMeasurementBuffers(
    GraphBufferId TriangleCount, uint TriangleStride, ulong FixedBytes, ulong CapacityBytes, ulong AllocationBytes, uint TriangleCapacity);

internal readonly record struct BlendDecalBuffers(
    uint ViewCount, ulong FixedBytes, ulong PropertyBytes,
    ulong CapacityBytes, ulong AllocationBytes);

internal sealed class ClusterBlendMeasurements : IGraphReadbackReceiver, IRenderGraphFrameExecutionObserver
{
    private const uint QueryCount = 12;
    private const ulong TimeBytes = QueryCount * sizeof(ulong);
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly QueryPool _queries;
    private readonly GraphReadbackId _readback;
    private readonly GraphQueryPoolId _queryId;
    private readonly GraphBufferId _results;
    private readonly GraphPassId _resolve;
    private readonly List<GraphPassId> _timestamps = [];
    private readonly ulong _frequency;
    private readonly BlendMeasurementPasses _passes;
    private BlendMeasurementBuffers _buffers;
    private readonly GraphPassId _colorEnd;
    private readonly (GraphPassId Start, GraphPassId End) _opaqueReceivers, _opaqueColors;
    private GraphPassId[] _receiverPasses = [], _colorPasses = [];
    private readonly Action<ClusterBlendMetrics> _completed;
    private readonly Dictionary<ulong, MeasuredFrame> _frames = [];
    private ulong _frameTag;

    private sealed class MeasuredFrame(int simulationFrame, bool reusedIndex, bool reusedDecalIndex,
        BlendMeasurementBuffers buffers, BlendDecalBuffers decals)
    {
        internal readonly int SimulationFrame = simulationFrame;
        internal readonly bool ReusedIndex = reusedIndex, ReusedDecalIndex = reusedDecalIndex;
        internal readonly BlendMeasurementBuffers Buffers = buffers;
        internal readonly BlendDecalBuffers Decals = decals;
        internal bool Submitted;
        internal ClusterBlendMetrics? Result;
    }

    internal ClusterBlendMeasurements(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, ref RenderGraphEdit edit,
        BlendMeasurementPasses passes, BlendMeasurementBuffers buffers, GraphBufferId decalWork, Action<ClusterBlendMetrics> completed)
    {
        _graph = graph;
        _passes = passes;
        _buffers = buffers;
        _completed = completed;
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        _queries = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, QueryCount,
            Label: "Cluster blend phase times"));
        GraphQueryPoolId queries = _queryId = edit.RegisterQueryPool(_queries);
        AddInterval(ref edit, queries, 0, passes.Geometry, passes.GeometryFirst, passes.GeometryLast);
        AddInterval(ref edit, queries, 2, passes.Requests, passes.Request, passes.Request);
        _colorEnd = AddInterval(ref edit, queries, 4, passes.Color, passes.ColorFirst, passes.Resolve, passes.ColorEnd).End;
        AddInterval(ref edit, queries, 6, passes.Decals, passes.DecalFirst, passes.DecalLast);
        _opaqueReceivers = AddInterval(ref edit, queries, 8, passes.OpaqueReceivers, default, default);
        _opaqueColors = AddInterval(ref edit, queries, 10, passes.OpaqueColors, default, default);
        ulong byteCount = TimeBytes + 32u;
        GraphBufferId results = _results = edit.CreateBuffer(new BufferDesc(byteCount,
            BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Cluster blend timing and work results"));
        GraphPassId resolve = _resolve = edit.AddGeneralFramePass("Resolve Cluster blend measurements",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Results(queries, results, buffers.TriangleCount, decalWork),
            new PassOptions(Culling: PassCullingMode.NeverCull), Results.Declare, Results.Record,
            region: passes.Feedback);
        for (int index = 1; index < _timestamps.Count; index += 2) edit.OrderAfter(resolve, _timestamps[index]);
        _readback = edit.AddReadback(results, BufferRange.Whole, this,
            "Observe Cluster blend measurements", passes.Feedback);
        edit.OrderAfter(_readback, resolve);
    }

    internal bool HasPasses(BlendMeasurementPasses passes) => _passes == passes;

    internal void UpdateOpaquePasses(ref RenderGraphEdit edit, GraphPassId[] receivers, GraphPassId[] colors)
    {
        Include(ref edit, _opaqueReceivers, ref _receiverPasses, receivers);
        Include(ref edit, _opaqueColors, ref _colorPasses, colors);

        static void Include(ref RenderGraphEdit graph, (GraphPassId Start, GraphPassId End) interval,
            ref GraphPassId[] previous, GraphPassId[] next)
        {
            if (previous.AsSpan().SequenceEqual(next)) return;
            // Reconciled group removal also removes that pass's timing edges.
            foreach (GraphPassId pass in next)
            {
                graph.OrderAfter(pass, interval.Start);
                graph.OrderAfter(interval.End, pass);
            }
            previous = next;
        }
    }

    internal void Stage(ref RenderGraphFrame graph, int simulationFrame, bool reusedIndex, bool reusedDecalIndex,
        BlendDecalBuffers decals)
    {
        _frameTag = graph.FrameIdentity;
        _frames.Add(graph.FrameIdentity, new(simulationFrame, reusedIndex, reusedDecalIndex, _buffers, decals));
        graph.SetReadbackTag(_readback, graph.FrameIdentity);
        graph.ObserveExecution(this);
    }

    internal void Remove(ref RenderGraphEdit edit)
    {
        foreach (GraphPassId timestamp in _timestamps) edit.Remove(timestamp);
        edit.Remove(_resolve);
        edit.Remove(_readback);
        edit.Remove(_results);
        edit.Remove(_queryId);
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
        ReadOnlySpan<ulong> times = MemoryMarshal.Cast<byte, ulong>(bytes[..(int)TimeBytes]);
        uint triangles = BinaryPrimitives.ReadUInt32LittleEndian(bytes[96..]);
        uint references = BinaryPrimitives.ReadUInt32LittleEndian(bytes[100..]);
        uint residentReferences = BinaryPrimitives.ReadUInt32LittleEndian(bytes[104..]);
        uint sources = BinaryPrimitives.ReadUInt32LittleEndian(bytes[108..]);
        uint decalReferences = BinaryPrimitives.ReadUInt32LittleEndian(bytes[112..]);
        uint decalResidentReferences = BinaryPrimitives.ReadUInt32LittleEndian(bytes[116..]);
        uint decalVolumes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[120..]);
        BlendMeasurementBuffers buffers = frame.Buffers;
        BlendDecalBuffers decals = frame.Decals;
        ulong decalUsed = decals.FixedBytes + (ulong)decalVolumes * (144u + decals.ViewCount * 32u) +
            (decalVolumes == 0 ? 0 : decals.PropertyBytes) + (ulong)decalResidentReferences * sizeof(uint);
        ulong used = buffers.FixedBytes + (ulong)Math.Min(triangles, buffers.TriangleCapacity) * buffers.TriangleStride +
            (ulong)residentReferences * sizeof(uint) + (ulong)sources * 12u;
        frame.Result = new(frame.SimulationFrame, tag,
            Milliseconds(times, 0), Milliseconds(times, 2), Milliseconds(times, 4),
            triangles, references, used + decalUsed, buffers.CapacityBytes + decals.CapacityBytes,
            buffers.AllocationBytes + decals.AllocationBytes, frame.ReusedIndex,
            Milliseconds(times, 6), Milliseconds(times, 8) + Milliseconds(times, 10),
            decalVolumes, decalReferences, decalResidentReferences,
            decalUsed, decals.CapacityBytes, decals.AllocationBytes, frame.ReusedDecalIndex);
        Accept(tag, frame);
    }

    private double Milliseconds(ReadOnlySpan<ulong> times, int first) =>
        1000.0 * (times[first + 1] - times[first]) / _frequency;

    private void Accept(ulong tag, MeasuredFrame frame)
    {
        if (!frame.Submitted || frame.Result is not { } result) return;
        _frames.Remove(tag);
        _completed(result);
    }

    internal void IncludeColorPass(ref RenderGraphEdit edit, GraphPassId complete, ulong capacityBytes, ulong allocationBytes)
    {
        edit.OrderAfter(_colorEnd, complete);
        _buffers = _buffers with { FixedBytes = _buffers.FixedBytes + capacityBytes,
            CapacityBytes = _buffers.CapacityBytes + capacityBytes, AllocationBytes = _buffers.AllocationBytes + allocationBytes };
    }

    private (GraphPassId Start, GraphPassId End) AddInterval(ref RenderGraphEdit edit, GraphQueryPoolId queries, uint index,
        GraphRegionId region, GraphPassId first, GraphPassId last, GraphRegionId endRegion = default)
    {
        GraphPassId start = edit.AddGeneralFramePass("Start Cluster blend phase timing",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Timestamp(queries, index),
            new PassOptions(Culling: PassCullingMode.NeverCull), Timestamp.Declare, Timestamp.Record, region: region);
        GraphPassId end = edit.AddGeneralFramePass("End Cluster blend phase timing",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Timestamp(queries, index + 1),
            new PassOptions(Culling: PassCullingMode.NeverCull), Timestamp.Declare, Timestamp.Record,
            region: endRegion == default ? region : endRegion);
        _timestamps.Add(start);
        _timestamps.Add(end);
        if (first != default) edit.OrderAfter(first, start);
        edit.OrderAfter(end, last == default ? start : last);
        return (start, end);
    }

    private readonly record struct Timestamp(GraphQueryPoolId Queries, uint Index)
    {
        internal static void Declare(ref PassDefinition pass, ref Timestamp data) => pass.Write(data.Queries, new QueryRange(data.Index, 1));
        internal static void Record(ref GeneralPassCommandScope commands, in Timestamp data) => commands.WriteTimestamp(data.Queries, data.Index);
    }

    private readonly record struct Results(GraphQueryPoolId Queries, GraphBufferId Destination,
        GraphBufferId TriangleCount, GraphBufferId DecalWork)
    {
        internal static void Declare(ref PassDefinition pass, ref Results data)
        {
            pass.Read(data.Queries, new QueryRange(0, QueryCount));
            if (data.TriangleCount != default)
                _ = pass.Read(data.TriangleCount, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Read(data.DecalWork, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref GeneralPassCommandScope commands, in Results data)
        {
            var destination = commands.GetBuffer(data.Destination);
            commands.ResolveQueries(data.Queries, 0, QueryCount, destination, new BufferRange(0, TimeBytes));
            if (data.TriangleCount == default) commands.ClearBuffer(destination, new BufferRange(TimeBytes, 16));
            else commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.TriangleCount), 0, destination, TimeBytes, 16));
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.DecalWork), 0, destination, TimeBytes + 16, 16));
        }
    }
}
