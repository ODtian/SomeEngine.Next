using System.Buffers.Binary;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private sealed class PageFaultReceiver(ClusterPipelineSystem pipeline, int capacity, ClusterEpochId epoch) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            int workOffset = checked(sizeof(uint) + capacity * 16);
            var work = (ClusterWorkOverflow)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(workOffset, sizeof(uint)));
            if (work != ClusterWorkOverflow.None)
                throw new ClusterWorkOverflowException(work, pipeline._options.MaxCandidates);
            pipeline._streamingFeedback.Publish(epoch.Value, tag, bytes[..workOffset], bytes[(workOffset + sizeof(uint))..]);
        }
    }

    private sealed class FrameMetricsReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => pipeline.ReceiveFrameMetrics(bytes, tag);
    }
}
