using SomeEngine.ECS;
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
    private Query _viewQuery;

    public LightingFeatureSystem(
        LightingFeature feature,
        RenderLightSetSystem lights,
        uint viewCount)
    {
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        _views = new RenderView[checked((int)viewCount)];
        _collector = new ViewCollector(_views.Length);
    }

    public void OnCreate(ref RenderFrameSystemContext context) =>
        _viewQuery = context.World.World.Query(new QueryDescription().WithStored<RenderView>());

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        _collector.Clear();
        using (QueryScope scope = _viewQuery.OpenRead()) _collector.Collect(scope);
        if (_collector.Count == 0 || _collector.Count > _views.Length)
        {
            throw new InvalidOperationException(
                $"Lighting requires 1..{_views.Length} render views.");
        }
        _collector.CopyTo(_views);
        RenderGraphFrame graph = context.Graph;
        var scheduler = context.World.World.Execution?.Scheduler;
        _feature.Record(
            ref graph,
            _lights.RequireCurrent(),
            _views.AsSpan(0, _collector.Count), enabled: true, scheduler: scheduler);
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        _viewQuery = default;
        _collector.Clear();
        Array.Clear(_views);
    }

    private sealed class ViewCollector
    {
        private readonly List<RenderView> _values;

        internal ViewCollector(int capacity) => _values = new List<RenderView>(capacity);

        internal int Count => _values.Count;

        internal void Collect(scoped QueryScope query)
        {
            foreach (QueryChunk chunk in query)
                foreach (RenderView value in chunk.Read<RenderView>())
                {
                    if (_values.Count == _values.Capacity)
                        throw new InvalidOperationException($"Lighting view capacity {_values.Capacity} was exceeded.");
                    _values.Add(value);
                }
        }

        internal void CopyTo(RenderView[] destination) =>
            _values.CopyTo(destination);

        internal void Clear() => _values.Clear();
    }
}
