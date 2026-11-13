using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal sealed class ClusterCullingHistory(uint viewCount)
{
    private readonly RenderView[] _views = new RenderView[viewCount];
    private readonly Entity[] _entities = new Entity[viewCount];
    internal int WriteIndex { get; private set; }
    internal bool RequiresInitialization { get; private set; } = true;

    internal HistorySubmission Prepare(ref RenderGraphFrame frame, scoped ReadOnlySpan<RenderView> views, scoped ReadOnlySpan<Entity> entities)
    {
        var data = new HistorySubmission(this, views.ToArray(), entities.ToArray());
        frame.ObserveExecution(data);
        return data;
    }

    internal sealed class HistorySubmission(ClusterCullingHistory history, RenderView[] views, Entity[] entities)
        : IRenderGraphFrameExecutionObserver
    {
        internal bool TryGetPrevious(int index, out Matrix4x4 view, out Matrix4x4 projection, out WorldOrigin origin)
        {
            RenderView previous = history._views[index];
            view = previous.View;
            projection = previous.Projection;
            origin = previous.WorldOrigin;
            return !history.RequiresInitialization && !views[index].CameraCut && entities[index] != Entity.Null &&
                entities[index] == history._entities[index] && views[index].PlayerId == previous.PlayerId;
        }
        internal Matrix4x4 PreviousFirstPersonTransform(int index) =>
            history._views[index].FirstPerson?.CalculateWorldToFirstPerson(history._views[index].View) ?? Matrix4x4.Identity;
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            views.CopyTo(history._views, 0);
            entities.CopyTo(history._entities, 0);
            history.RequiresInitialization = false;
            history.WriteIndex = 1 - history.WriteIndex;
        }
    }

    internal static int CalculateMipCount(int width, int height) => 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
}
