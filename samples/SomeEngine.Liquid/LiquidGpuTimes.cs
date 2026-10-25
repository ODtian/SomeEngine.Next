using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Liquid;

public sealed class LiquidGpuTimes : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly QueryPool _queries;
    private readonly Buffer _readback;
    private readonly ulong _frequency;
    private readonly uint _count;
    public GraphQueryPoolId Queries { get; }

    public LiquidGpuTimes(ApplicationRendering rendering, ref RenderGraphEdit edit, int stages)
    {
        _backend = rendering.Backend;
        _count = (uint)stages * 2;
        _frequency = _backend.CalibrateTimestamps(_backend.GetQueue(rendering.Device, QueueType.Graphics)).QueueFrequency;
        _queries = _backend.CreateQueryPool(rendering.Device, new(QueryType.Timestamp, QueueType.Graphics, _count));
        _readback = _backend.CreateBuffer(rendering.Device,
            new(_count * 8ul, BufferUsages.QueryResolve | BufferUsages.CopyDestination), MemoryType.Readback);
        Queries = edit.RegisterQueryPool(_queries);
    }

    public GraphPassId Resolve(ref RenderGraphEdit edit, GraphPassId after)
    {
        GraphBufferId buffer = edit.RegisterExternalBuffer(_readback,
            [new(BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
        GraphPassId pass = edit.AddCopyFramePass("Read liquid GPU times", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new QueryCopy(Queries, buffer, _count), new PassOptions(RepeatCommands: true),
            static (ref PassDefinition definition, ref QueryCopy data) =>
            {
                definition.Read(data.Pool, new(0, data.Count));
                definition.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in QueryCopy data) =>
                commands.ResolveQueries(data.Pool, 0, data.Count, commands.GetBuffer(data.Buffer), BufferRange.Whole));
        edit.OrderAfter(pass, after);
        return pass;
    }

    public double[] Read()
    {
        using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> timestamps = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        var result = new double[_count / 2];
        for (int stage = 0; stage < result.Length; stage++)
            result[stage] = (timestamps[stage * 2 + 1] - timestamps[stage * 2]) * 1000.0 / _frequency;
        return result;
    }

    public void Dispose() { _readback.Dispose(); _queries.Dispose(); }
    private readonly record struct QueryCopy(GraphQueryPoolId Pool, GraphBufferId Buffer, uint Count);
}
