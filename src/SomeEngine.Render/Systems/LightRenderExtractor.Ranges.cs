using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Systems;

public sealed partial class LightRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    public IRenderExtractionCollector CreateRangeCollector() => new LightRenderExtractor();
    public void Dispose() => Reset();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (LightRenderExtractor)collector;
        _directional.AddRange(values._directional);
        _points.AddRange(values._points);
        _spots.AddRange(values._spots);
        _directionalSources.UnionWith(values._directionalSources);
        _pointSources.UnionWith(values._pointSources);
        _spotSources.UnionWith(values._spotSources);
        _cookieSources.UnionWith(values._cookieSources);
        return values._directional.Count + values._points.Count + values._spots.Count != 0;
    }
}
