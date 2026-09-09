using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private void ReconcileTraversal()
    {
        ClusterFrameUploads uploads = _uploads ?? throw new InvalidOperationException("Cluster uploads are unavailable.");
        int previousCount = _traversalLevelPasses.Count;
        GraphPassId primary = _traversalPass;
        GraphPassId primaryPost = _primaryPostTraversalPass;
        GraphPassId continuation = _continuationTraversalPass;
        GraphPassId continuationPost = _continuationPostTraversalPass;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            uploads.BeginDeclarations();
            ulong size = checked(((ulong)_resources.HierarchyLevels + 1u) * TraversalArgumentStride);
            BufferUsages usages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination | BufferUsages.Indirect;
            edit.SetBufferDescription(_frameResources.TraversalArguments0,
                new BufferDesc(size, usages, "Cluster hierarchy indirect arguments 0"));
            edit.SetBufferDescription(_frameResources.TraversalArguments1,
                new BufferDesc(size, usages, "Cluster hierarchy indirect arguments 1"));
            _traversalPass = Extend(ref edit, _traversalLevelPasses, primary,
                _phaseOneCullPass, 0u, "primary");
            _primaryPostTraversalPass = Extend(ref edit, _primaryPostTraversalLevelPasses, primaryPost,
                _phaseTwoCullPass, 2u, "primary post-HiZ");
            if (_renderContext.IsValid)
            {
                _continuationTraversalPass = Extend(ref edit, _continuationTraversalLevelPasses, continuation,
                    _continuationCullPass, 1u, "continuation");
                _continuationPostTraversalPass = Extend(ref edit, _continuationPostTraversalLevelPasses, continuationPost,
                    _continuationPostCullPass, 2u, "continuation post-HiZ");
            }
            uploads.RemoveUnusedDeclarations(ref edit);
            RecordUploadFinalization(ref edit);
            edit.Commit();
            uploads.CompleteDeclarations(_graph);
        }
        catch
        {
            uploads.CancelDeclarations();
            Trim(_traversalLevelPasses);
            Trim(_primaryPostTraversalLevelPasses);
            Trim(_continuationTraversalLevelPasses);
            Trim(_continuationPostTraversalLevelPasses);
            _traversalPass = primary;
            _primaryPostTraversalPass = primaryPost;
            _continuationTraversalPass = continuation;
            _continuationPostTraversalPass = continuationPost;
            throw;
        }
        finally
        {
            edit.Dispose();
        }

        void Trim(List<GraphPassId> passes)
        {
            if (passes.Count > previousCount)
                passes.RemoveRange(previousCount, passes.Count - previousCount);
        }
    }

    private GraphPassId Extend(ref RenderGraphEdit edit, List<GraphPassId> passes,
        GraphPassId previous, GraphPassId consumer, uint mode, string label)
    {
        int firstNewPass = passes.Count;
        GraphPassId last = RecordTraversal(ref edit, _frameResources, _frameResources.PackedViews,
            _frameResources.ViewCount, 0u, mode, false, previous, passes, label, out _);
        bool policyTarget = ReferenceEquals(passes, _continuationTraversalLevelPasses) ||
            ReferenceEquals(passes, _continuationPostTraversalLevelPasses);
        for (int index = firstNewPass; index < passes.Count; index++)
            BindTargetPolicyResources(ref edit, passes[index], policyTarget);
        edit.RemoveOrder(consumer, previous);
        edit.OrderAfter(consumer, last);
        return last;
    }
}
