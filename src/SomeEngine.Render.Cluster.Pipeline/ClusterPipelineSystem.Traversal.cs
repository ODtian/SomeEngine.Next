using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private void ReconcileTraversal()
    {
        _targetFirstRaster = null;
        _targetSecondRaster = null;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            ulong size = checked(((ulong)_resources.HierarchyLevels + 1u) * TraversalArgumentStride);
            BufferUsages usages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination | BufferUsages.Indirect;
            edit.SetBufferDescription(GraphData.Resources.TraversalArguments0,
                new BufferDesc(size, usages, "Cluster hierarchy indirect arguments 0"));
            edit.SetBufferDescription(GraphData.Resources.TraversalArguments1,
                new BufferDesc(size, usages, "Cluster hierarchy indirect arguments 1"));
            foreach ((int phase, GraphRegionMap<int, bool> levels) in GraphData.TraversalLevels)
                GraphData.TraversalLevels = GraphData.TraversalLevels.SetItem(phase, RecordTraversal(ref edit, GraphData.Resources,
                    GraphData.Resources.PackedViews, phase < 2 ? GraphData.Resources.ViewCount : _renderContext.MaximumInvocationCount,
                    phase < 2 ? 0u : 1u, phase % 2 != 0 ? 2u : phase == 0 ? 0u : 1u,
                    false, levels, phase switch
                    {
                        0 => "primary", 1 => "primary post-HiZ", 2 => "continuation", _ => "continuation post-HiZ",
                    }));
        });
    }
}
