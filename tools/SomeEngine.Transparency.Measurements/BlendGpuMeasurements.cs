using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;

namespace SomeEngine.Transparency.Measurements;

internal readonly record struct BlendMeasurementPasses(
    GraphPassId[] Visibility, GraphPassId[] Classification, GraphPassId[] Shading, GraphPassId[] Composite,
    GraphRegionId Feedback, GraphPassId[] Decals, GraphPassId[] OpaqueRequests, GraphPassId[] OpaqueShading);

internal readonly record struct BlendMeasurementBuffers(GraphBufferId FrameCounters,
    ulong RecordStride, ulong PayloadStride, ulong BlockStride, ulong FixedBytes,
    ulong BlockPoolCapacityBytes, ulong CapacityBytes, ulong AllocationBytes);

internal readonly record struct BlendDecalBuffers(uint VolumeCount, ulong FixedBytes, ulong CellBytes,
    ulong PropertyBytes, ulong CapacityBytes, ulong AllocationBytes);

internal sealed class BlendGpuMeasurements : IGraphReadbackReceiver
{
    private enum Phase { Visibility, Classification, Shading, Composite, OpaqueRequests, OpaqueShading, Decals }
    private readonly record struct PassTime(GraphPassId Pass, Phase Phase);
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly QueryPool _queries;
    private readonly GraphReadbackId _readback;
    private readonly GraphQueryPoolId _queryId;
    private readonly GraphBufferId _results;
    private readonly GraphPassId _resolve;
    private readonly PassTime[] _times;
    private readonly ulong _frequency;
    private readonly BlendMeasurementPasses _passes;
    private readonly GraphBufferId _decalWork;
    private readonly Action<BlendFrameMetrics> _completed;
    private readonly Dictionary<ulong, MeasuredFrame> _frames = [];

    private sealed class MeasuredFrame(int simulationFrame, bool reusedDecalBinning,
        BlendMeasurementBuffers buffers, BlendDecalBuffers decals, bool[] active)
    {
        internal readonly int SimulationFrame = simulationFrame;
        internal readonly bool ReusedDecalBinning = reusedDecalBinning;
        internal readonly BlendMeasurementBuffers Buffers = buffers;
        internal readonly BlendDecalBuffers Decals = decals;
        internal readonly bool[] Active = active;
    }

    public BlendGpuMeasurements(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, ref RenderGraphEdit edit,
        BlendMeasurementPasses passes, GraphBufferId decalWork, Action<BlendFrameMetrics> completed)
    {
        (_graph, _passes, _decalWork, _completed) = (graph, passes, decalWork, completed);
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        var times = new List<PassTime>();
        times.AddRange(passes.Visibility.Select(pass => new PassTime(pass, Phase.Visibility)));
        times.AddRange(passes.Classification.Select(pass => new PassTime(pass, Phase.Classification)));
        times.AddRange(passes.Shading.Select(pass => new PassTime(pass, Phase.Shading)));
        times.AddRange(passes.Composite.Select(pass => new PassTime(pass, Phase.Composite)));
        times.AddRange(passes.OpaqueRequests.Select(pass => new PassTime(pass, Phase.OpaqueRequests)));
        times.AddRange(passes.OpaqueShading.Select(pass => new PassTime(pass, Phase.OpaqueShading)));
        times.AddRange(passes.Decals.Select(pass => new PassTime(pass, Phase.Decals)));
        _times = times.Distinct().ToArray();
        uint queryCount = (uint)_times.Length * 2u;
        _queries = backend.CreateQueryPool(device, new(QueryType.Timestamp, QueueType.Graphics, queryCount,
            Label: "Layer effect pass times"));
        _queryId = edit.RegisterQueryPool(_queries);
        for (int index = 0; index < _times.Length; index++)
            edit.MeasurePassTime(_times[index].Pass, _queryId, (uint)index * 2u);
        _results = edit.CreateBuffer(new((ulong)_times.Length * 16u + 52u,
            BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Layer effect times and work"));
        _resolve = edit.AddGeneralFramePass("Resolve layer effect measurements",
            PassQueueSelection.AnyOfType(QueueType.Graphics), new Results(_queryId, _results, default, decalWork, []),
            new PassOptions(Culling: PassCullingMode.NeverCull), Results.Declare, Results.Record, passes.Feedback);
        _readback = edit.AddReadback(_results, BufferRange.Whole, this, "Observe layer effect measurements", passes.Feedback);
        edit.OrderAfter(_readback, _resolve);
    }

    public bool HasPasses(BlendMeasurementPasses passes) => _passes.Feedback == passes.Feedback &&
        _passes.Visibility.AsSpan().SequenceEqual(passes.Visibility) &&
        _passes.Classification.AsSpan().SequenceEqual(passes.Classification) &&
        _passes.Shading.AsSpan().SequenceEqual(passes.Shading) &&
        _passes.Composite.AsSpan().SequenceEqual(passes.Composite) &&
        _passes.Decals.AsSpan().SequenceEqual(passes.Decals) &&
        _passes.OpaqueRequests.AsSpan().SequenceEqual(passes.OpaqueRequests) &&
        _passes.OpaqueShading.AsSpan().SequenceEqual(passes.OpaqueShading);

    public void Stage(ref RenderGraphFrame graph, int simulationFrame, bool reusedDecalBinning,
        BlendMeasurementBuffers buffers, BlendDecalBuffers decals)
    {
        var frame = new MeasuredFrame(simulationFrame, reusedDecalBinning, buffers, decals, new bool[_times.Length]);
        graph.SetReadbackTag(_readback, graph.FrameIdentity);
        PassDefinition resolve = graph.GetPass(_resolve);
        for (int index = 0; index < _times.Length; index++)
        {
            frame.Active[index] = graph.IsPassEnabled(_times[index].Pass);
            if (frame.Active[index]) resolve.Read(_queryId, new QueryRange((uint)index * 2u, 2));
        }
        if (buffers.FrameCounters != default)
            resolve.Read(buffers.FrameCounters, new(0, 32), PipelineSync.Copy, ResourceAccess.CopySource);
        graph.SetPassData(_resolve, new Results(_queryId, _results, buffers.FrameCounters, _decalWork, frame.Active));
        graph.ObserveExecution(new MeasurementSubmission(this, frame));
    }

    private sealed class MeasurementSubmission(BlendGpuMeasurements measurements, MeasuredFrame frame)
        : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            if (submission.TryGetReadbackCompletion(measurements._readback, out _))
                measurements._frames.Add(submission.FrameIdentity, frame);
        }
    }

    public void Remove(ref RenderGraphEdit edit)
    {
        edit.Remove(_readback);
        edit.Remove(_resolve);
        edit.Remove(_results);
        edit.Remove(_queryId);
        _graph.RetireAfterSubmittedFrames(_queries);
    }

    public void ReleaseQueries() => _queries.Dispose();

    public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
    {
        MeasuredFrame frame = _frames[tag];
        _frames.Remove(tag);
        int timeBytes = _times.Length * 16;
        ReadOnlySpan<ulong> times = MemoryMarshal.Cast<byte, ulong>(bytes[..timeBytes]);
        Span<double> milliseconds = stackalloc double[7];
        milliseconds.Clear();
        for (int index = 0; index < _times.Length; index++)
            if (frame.Active[index])
                milliseconds[(int)_times[index].Phase] += 1000.0 * (times[index * 2 + 1] - times[index * 2]) / _frequency;
        LayerCompositionMetrics layers = LayerCompositionMetrics.Read(bytes.Slice(timeBytes, 32), tag);
        ReadOnlySpan<byte> decalWork = bytes.Slice(timeBytes + 32, 20);
        uint references = BinaryPrimitives.ReadUInt32LittleEndian(decalWork[4..]);
        uint retained = BinaryPrimitives.ReadUInt32LittleEndian(decalWork[16..]);
        BlendMeasurementBuffers buffers = frame.Buffers;
        BlendDecalBuffers decals = frame.Decals;
        ulong records = layers.Fragments * buffers.RecordStride;
        ulong payloads = layers.Fragments * buffers.PayloadStride;
        ulong pool = layers.BlocksUsed * buffers.BlockStride;
        ulong decalUsed = decals.FixedBytes + decals.CellBytes + (ulong)decals.VolumeCount * 144u +
            (decals.VolumeCount == 0 ? 0 : decals.PropertyBytes) + (ulong)retained * sizeof(uint);
        _completed(new(frame.SimulationFrame, tag,
            milliseconds[(int)Phase.Visibility], milliseconds[(int)Phase.Classification],
            milliseconds[(int)Phase.Shading], milliseconds[(int)Phase.Composite],
            milliseconds[(int)Phase.OpaqueRequests], milliseconds[(int)Phase.OpaqueShading], milliseconds[(int)Phase.Decals],
            layers, records, payloads, pool, buffers.BlockPoolCapacityBytes,
            buffers.FixedBytes + pool + payloads, buffers.CapacityBytes, buffers.AllocationBytes,
            decals.VolumeCount, references, retained, decalUsed, decals.CapacityBytes, decals.AllocationBytes,
            frame.ReusedDecalBinning));
    }

    private readonly record struct Results(GraphQueryPoolId Queries, GraphBufferId Destination,
        GraphBufferId FrameCounters, GraphBufferId DecalWork, bool[] Active)
    {
        internal static void Declare(ref PassDefinition pass, ref Results data)
        {
            if (data.DecalWork != default)
                pass.Read(data.DecalWork, new(0, 20), PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
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
            ulong offset = (ulong)data.Active.Length * 16u;
            if (data.FrameCounters == default) commands.ClearBuffer(destination, new(offset, 32));
            else commands.CopyBuffer(new(commands.GetBuffer(data.FrameCounters), 0, destination, offset, 32));
            if (data.DecalWork == default) commands.ClearBuffer(destination, new(offset + 32, 20));
            else commands.CopyBuffer(new(commands.GetBuffer(data.DecalWork), 0, destination, offset + 32, 20));
        }
    }
}
