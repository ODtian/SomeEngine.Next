using System.Runtime.CompilerServices;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private static readonly ConditionalWeakTable<ClusterRenderResources, GeometryFrame> GeometryFrames = new();

    private bool BeginGeometryFrame(ref RenderGraphFrame frame, ClusterResidencyPublication? changes) =>
        GeometryFrames.GetValue(_resources, static _ => new GeometryFrame()).Begin(this, ref frame, changes);

    // All views consume one geometry update. View-local history still has its own observer.
    private sealed class GeometryFrame : IRenderGraphFrameExecutionObserver
    {
        private ClusterPipelineSystem? _first;
        private ClusterResidencyPublication? _changes;
        private ulong _frame;
        private readonly HashSet<GraphReadbackId> _readbacks = [];

        internal bool Begin(ClusterPipelineSystem pipeline, ref RenderGraphFrame frame, ClusterResidencyPublication? changes)
        {
            bool first = _first is null;
            if (first)
            {
                _first = pipeline;
                _frame = frame.FrameIdentity;
                _changes = changes;
                frame.ObserveExecution(this);
            }
            else if (_frame != frame.FrameIdentity || !ReferenceEquals(_first!._graph, pipeline._graph) ||
                !ReferenceEquals(_first._streamingFeedback, pipeline._streamingFeedback) || !ReferenceEquals(_changes, changes))
            {
                throw new InvalidOperationException("Shared Cluster geometry requires one graph frame and feedback channel.");
            }
            if (!first && changes is { Meshes.LeafFlagFixups.Length: > 0 })
            {
                GraphPassId fixups = _first!.GraphData.LeafFlagPass;
                frame.OrderAfter(pipeline.GraphData.SceneBounds.Pass, fixups);
                // Bounds can be unchanged and disabled while streamed leaf flags still change.
                foreach (GraphPassId traversal in pipeline.GraphData.Resources.Work.Phases)
                    if (traversal != default) frame.OrderAfter(traversal, fixups);
            }
            _readbacks.Add(pipeline.GraphData.PageFaultReadback);
            return first;
        }

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            ClusterPipelineSystem pipeline = _first!;
            try
            {
                int feedbackCount = 0;
                foreach (GraphReadbackId readback in _readbacks)
                    if (result.TryGetReadbackCompletion(readback, out _)) feedbackCount++;
                pipeline._streamingFeedback.ExpectFrame(pipeline._resources.EpochId.Value, _frame, feedbackCount);
                if (_changes is not { } changes) return;
                bool uploaded = !result.SubmittedCompletions.IsEmpty;
                if (changes.Meshes.PageUploads.Length != 0)
                    uploaded &= result.TryGetPassCompletion(pipeline.GraphData.PageUpload, out _);
                if (changes.Meshes.Bvh.Uploads.Length != 0)
                    uploaded &= result.TryGetPassCompletion(pipeline.GraphData.BvhUpload, out _);
                if (changes.Meshes.LeafFlagFixups.Length != 0)
                    uploaded &= result.TryGetPassCompletion(pipeline.GraphData.LeafFlagPass, out _);
                if (uploaded)
                    pipeline._resources.CommitResidencyPublication(changes, pipeline.CaptureRetirementCompletions(changes));
                else
                    pipeline._resources.RetainResidencyPublication(changes);
            }
            finally
            {
                _first = null;
                _changes = null;
                _readbacks.Clear();
            }
        }
    }
}
