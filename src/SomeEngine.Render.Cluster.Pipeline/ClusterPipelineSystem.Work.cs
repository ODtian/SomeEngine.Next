using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
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
    }
}
