using System.Buffers.Binary;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private ClusterWorkOverflow _workOverflow;

    /// <summary>Rejects completed frames whose required geometry work could not be retained.
    /// Call after collecting graph readbacks, including the last submitted frame.</summary>
    public void ThrowIfWorkFailed()
    {
        if (_workOverflow != ClusterWorkOverflow.None)
            throw new ClusterWorkOverflowException(_workOverflow, _options.MaxCandidates);
    }

    private sealed class WorkStatusReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) =>
            pipeline._workOverflow |= (ClusterWorkOverflow)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private void BindWorkStatus(ref RenderGraphEdit graph, ref PassDefinition pass) =>
        _ = pass.Bind(graph.CreateBufferUav(GraphData.Resources.WorkStatus), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);

    private void RecordWorkStatus(ref RenderGraphEdit graph, in FrameResources frame)
    {
        var clear = new ClusterBufferClearParameters(frame.WorkStatus);
        graph.AddCopyFramePass("Reset Cluster work overflow status", PassQueueSelection.AnyCompatible(), clear,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition pass, ref ClusterBufferClearParameters data) =>
                _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete),
            ClusterBufferClearParameters.Record, region: _regions.PrimaryGeometry);
        GraphData.WorkStatusReadback = graph.AddReadback(frame.WorkStatus, BufferRange.Whole,
            new WorkStatusReceiver(this), "Read back Cluster work overflow status", _regions.Feedback);
    }
}
