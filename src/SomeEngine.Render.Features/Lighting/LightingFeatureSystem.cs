using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Records lighting before geometry shade passes without adding lighting state to Cluster.</summary>
public sealed class LightingFeatureSystem : ISystem<RenderFrameSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "Lighting.Update", detail: true);

    private readonly LightingFeature _feature;
    private readonly RenderLightSetSystem _lights;
    private readonly RenderView[] _views;
    private readonly ViewCollector _collector;
    private QueryHandle _viewQuery;

    public LightingFeatureSystem(
        LightingFeature feature,
        RenderLightSetSystem lights,
        uint viewCount)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        _views = new RenderView[checked((int)viewCount)];
        _collector = new ViewCollector(_views);
    }

    public void OnCreate(ref RenderFrameSystemContext context) =>
        _viewQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>());

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        _collector.Clear();
        ViewCollector collector = _collector;
        context.World.ExecuteQuery(
            _viewQuery,
            ref collector,
            static (QueryCursor cursor, ref ViewCollector collector) => collector.Collect(cursor));
        if (_collector.Count == 0 || _collector.Count > _views.Length)
        {
            throw new InvalidOperationException(
                $"Lighting requires 1..{_views.Length} render views.");
        }
        RenderGraphFrame graph = context.Graph;
        _feature.Record(
            ref graph,
            _lights.RequireCurrent(),
            _views.AsSpan(0, _collector.Count));
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (_viewQuery.IsValid)
            context.World.ReleaseQuery(_viewQuery);
        _viewQuery = default;
        _collector.Clear();
        Array.Clear(_views);
    }

    private sealed class ViewCollector
    {
        private readonly RenderView[] _values;

        internal ViewCollector(RenderView[] values) => _values = values;

        internal int Count { get; private set; }

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                if (Count == _values.Length)
                {
                    throw new InvalidOperationException(
                        $"Lighting view capacity {_values.Length} was exceeded.");
                }
                _values[Count] = row.Read<RenderView>();
                Count++;
            }
        }

        internal void Clear() => Count = 0;
    }
}
