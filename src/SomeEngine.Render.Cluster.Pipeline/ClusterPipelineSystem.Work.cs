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
        graph.AddCopyFramePass("Reset Cluster work overflow status", PassQueueSelection.AnyCompatible(), frame.WorkStatus,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            DeclareBufferClear,
            RecordBufferClear, region: _regions.PrimaryGeometry);
    }
}
