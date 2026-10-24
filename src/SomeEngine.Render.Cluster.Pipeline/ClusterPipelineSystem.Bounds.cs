using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly IClusterBoundsSource? _deformBounds;
    private ulong _sceneDeformRevision;

    private void BindDeformBounds(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        if (_deformBounds is { } bounds)
        {
            pass.Bind(graph.CreateBufferSrv(bounds.CurrentBounds, structureStride: 16), PipelineSync.ComputeShading);
            pass.Bind(graph.CreateBufferSrv(bounds.PreviousBounds, structureStride: 16), PipelineSync.ComputeShading);
            graph.OrderAfter(pass.Id, bounds.CompletePass);
        }
        else
        {
            pass.Bind(graph.CreateBufferSrv(GraphData.Resources.NeutralPolicyView, structureStride: 16), PipelineSync.ComputeShading);
            pass.Bind(graph.CreateBufferSrv(GraphData.Resources.NeutralPolicyView, structureStride: 16), PipelineSync.ComputeShading);
        }
    }
}
