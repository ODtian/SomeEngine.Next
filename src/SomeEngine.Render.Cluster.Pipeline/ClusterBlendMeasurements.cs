using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct BlendMeasurementPasses(
    GraphRegionId Geometry, GraphPassId GeometryFirst, GraphPassId GeometryLast,
    GraphRegionId Requests, GraphPassId Request,
    GraphRegionId Color, GraphPassId ColorFirst, GraphPassId Resolve, GraphRegionId Feedback);

internal readonly record struct BlendMeasurementBuffers(
    GraphBufferId TriangleCount, uint TriangleStride, ulong FixedBytes, ulong CapacityBytes, ulong AllocationBytes, uint TriangleCapacity);

internal sealed class ClusterBlendMeasurements : IGraphReadbackReceiver, IRenderGraphFrameExecutionObserver
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly QueryPool _queries;
    private readonly GraphReadbackId _readback;
    private readonly GraphQueryPoolId _queryId;
    private readonly GraphBufferId _results;
    private readonly GraphPassId _resolve;
    private readonly List<GraphPassId> _timestamps = [];
    private readonly ulong _frequency;
    private BlendMeasurementBuffers _buffers;
    private readonly GraphPassId _colorEnd;
    private readonly Action<ClusterBlendMetrics> _completed;
    private readonly Dictionary<ulong, MeasuredFrame> _frames = [];
    private ulong _frameTag;

    private sealed class MeasuredFrame(int simulationFrame, bool reusedIndex)
    {
        internal readonly int SimulationFrame = simulationFrame;
        internal readonly bool ReusedIndex = reusedIndex;
        internal bool Submitted;
        internal ClusterBlendMetrics? Result;
    }

    internal ClusterBlendMeasurements(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, ref RenderGraphEdit edit,
        BlendMeasurementPasses passes, BlendMeasurementBuffers buffers, Action<ClusterBlendMetrics> completed)
    {
        _graph = graph;
        _buffers = buffers;
        _completed = completed;
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        _queries = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 6,
            Label: "Cluster blend phase times"));
        GraphQueryPoolId queries = _queryId = edit.RegisterQueryPool(_queries);
        AddInterval(ref edit, queries, 0, passes.Geometry, passes.GeometryFirst, passes.GeometryLast);
        AddInterval(ref edit, queries, 2, passes.Requests,
            passes.Request == default ? passes.GeometryLast : passes.Request,
            passes.Request == default ? passes.GeometryLast : passes.Request,
            empty: passes.Request == default);
        _colorEnd = AddInterval(ref edit, queries, 4, passes.Color, passes.ColorFirst, passes.Resolve);
        ulong byteCount = 6u * sizeof(ulong) + 16u;
        GraphBufferId results = _results = edit.CreateBuffer(new BufferDesc(byteCount,
            BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Cluster blend timing and work results"));
        GraphPassId resolve = _resolve = edit.AddGeneralFramePass("Resolve Cluster blend measurements",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Results(queries, results, buffers),
            new PassOptions(Culling: PassCullingMode.NeverCull), Results.Declare, Results.Record,
            region: passes.Feedback);
        edit.OrderAfter(resolve, passes.Resolve);
        _readback = edit.AddReadback(results, BufferRange.Whole, this,
            "Observe Cluster blend measurements", passes.Feedback);
        edit.OrderAfter(_readback, resolve);
    }

    internal void Stage(ref RenderGraphFrame graph, int simulationFrame, bool reusedIndex)
    {
        _frameTag = graph.FrameIdentity;
        _frames.Add(graph.FrameIdentity, new(simulationFrame, reusedIndex));
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
        ReadOnlySpan<ulong> times = MemoryMarshal.Cast<byte, ulong>(bytes[..48]);
        uint triangles = BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..]);
        uint references = BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]);
        frame.Result = new(frame.SimulationFrame, tag,
            1000.0 * (times[1] - times[0]) / _frequency,
            1000.0 * (times[3] - times[2]) / _frequency,
            1000.0 * (times[5] - times[4]) / _frequency,
            triangles, references,
            _buffers.FixedBytes + (ulong)Math.Min(triangles, _buffers.TriangleCapacity) * _buffers.TriangleStride +
                (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]) * sizeof(uint) +
                (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]) * 12u,
            _buffers.CapacityBytes, _buffers.AllocationBytes, frame.ReusedIndex);
        Accept(tag, frame);
    }

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

    private GraphPassId AddInterval(ref RenderGraphEdit edit, GraphQueryPoolId queries, uint index,
        GraphRegionId region, GraphPassId first, GraphPassId last, bool empty = false)
    {
        GraphPassId start = edit.AddGeneralFramePass("Start Cluster blend phase timing",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Timestamp(queries, index),
            new PassOptions(Culling: PassCullingMode.NeverCull), Timestamp.Declare, Timestamp.Record, region: region);
        GraphPassId end = edit.AddGeneralFramePass("End Cluster blend phase timing",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Timestamp(queries, index + 1),
            new PassOptions(Culling: PassCullingMode.NeverCull), Timestamp.Declare, Timestamp.Record, region: region);
        _timestamps.Add(start);
        _timestamps.Add(end);
        if (empty) edit.OrderAfter(start, last);
        else edit.OrderAfter(first, start);
        edit.OrderAfter(end, empty ? start : last);
        return end;
    }

    private readonly record struct Timestamp(GraphQueryPoolId Queries, uint Index)
    {
        internal static void Declare(ref PassDefinition pass, ref Timestamp data) => pass.Write(data.Queries, new QueryRange(data.Index, 1));
        internal static void Record(ref GeneralPassCommandScope commands, in Timestamp data) => commands.WriteTimestamp(data.Queries, data.Index);
    }

    private readonly record struct Results(GraphQueryPoolId Queries, GraphBufferId Destination, BlendMeasurementBuffers Buffers)
    {
        internal static void Declare(ref PassDefinition pass, ref Results data)
        {
            pass.Read(data.Queries, new QueryRange(0, 6));
            _ = pass.Read(data.Buffers.TriangleCount, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref GeneralPassCommandScope commands, in Results data)
        {
            var destination = commands.GetBuffer(data.Destination);
            commands.ResolveQueries(data.Queries, 0, 6, destination, new BufferRange(0, 48));
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Buffers.TriangleCount), 0, destination, 48, 16));
        }
    }
}
