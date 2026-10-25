using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Systems;

public sealed partial class XrayRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    public IRenderExtractionCollector CreateRangeCollector() => new XrayRenderExtractor();
    public void Dispose() => Reset();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (XrayRenderExtractor)collector;
        _effects.AddRange(values._effects);
        _sources.UnionWith(values._sources);
        return values._effects.Count != 0;
    }
}
