using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Stages the receiver and shadow multi-view work before the two Cluster systems record.</summary>
public sealed class VirtualShadowFeatureSystem : ISystem<RenderFrameSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "VirtualShadows.Update", detail: true);

    private readonly VirtualShadowFeature _feature;
    private readonly RenderLightSetSystem _lights;
    private QueryHandle _viewQuery;

    public VirtualShadowFeatureSystem(
        VirtualShadowFeature feature,
        RenderLightSetSystem lights)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
    }

    public void OnCreate(ref RenderFrameSystemContext context) =>
        _viewQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>());

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        RenderView receiver = default;
        int count = 0;
        var collector = new ReceiverCollector();
        context.World.ExecuteQuery(
            _viewQuery,
            ref collector,
            static (QueryCursor cursor, ref ReceiverCollector value) => value.Collect(cursor));
        receiver = collector.View;
        count = collector.Count;
        if (count != 1)
            throw new InvalidOperationException("Virtual shadows require exactly one receiver RenderView.");

        var graph = context.Graph;
        _feature.StageFrame(ref graph, _lights.RequireCurrent(), receiver, collector.Entity);
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (_viewQuery.IsValid)
            context.World.ReleaseQuery(_viewQuery);
        _viewQuery = default;
    }

    private struct ReceiverCollector
    {
        internal RenderView View;
        internal Entity Entity;
        internal int Count;

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                if (++Count > 1)
                    return;
                View = row.Read<RenderView>();
                Entity = row.Entity;
            }
        }
    }
}
