using System.Buffers.Binary;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private sealed class PageFaultReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            var work = (ClusterWorkOverflow)BinaryPrimitives.ReadUInt32LittleEndian(bytes[^sizeof(uint)..]);
            if (work != ClusterWorkOverflow.None)
                throw new ClusterWorkOverflowException(work, pipeline._options.MaxCandidates);
            pipeline._streamingFeedback.Publish(tag, bytes[..^sizeof(uint)]);
        }
    }

    private sealed class FrameMetricsReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => pipeline.ReceiveFrameMetrics(bytes, tag);
    }
}
