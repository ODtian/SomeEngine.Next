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
    private readonly LightingFeature _feature;
    private readonly RenderLightSetSystem _lights;
    private readonly RenderView[] _views;
    private readonly ViewCollector _collector;
    private QueryHandle _viewQuery;

    public LightingFeatureSystem(
        LightingFeature feature,
        RenderLightSetSystem lights,
        uint viewCapacity)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        ArgumentOutOfRangeException.ThrowIfZero(viewCapacity);
        _views = new RenderView[checked((int)viewCapacity)];
        _collector = new ViewCollector(_views.Length);
    }

    public void OnCreate(ref RenderFrameSystemContext context) =>
        _viewQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>());

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
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
        _collector.CopyTo(_views);
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
        private readonly List<RenderView> _values;

        internal ViewCollector(int capacity) => _values = new List<RenderView>(capacity);

        internal int Count => _values.Count;

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                if (_values.Count == _values.Capacity)
                {
                    throw new InvalidOperationException(
                        $"Lighting view capacity {_values.Capacity} was exceeded.");
                }
                _values.Add(row.Read<RenderView>());
            }
        }

        internal void CopyTo(RenderView[] destination) =>
            _values.CopyTo(destination);

        internal void Clear() => _values.Clear();
    }
}
