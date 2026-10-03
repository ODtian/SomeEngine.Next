using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private sealed class PageFaultReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) =>
            pipeline._streamingFeedback.Publish(tag, bytes);
    }

    private sealed class FrameMetricsReceiver(ClusterPipelineSystem pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => pipeline.ReceiveFrameMetrics(bytes, tag);
    }
}
