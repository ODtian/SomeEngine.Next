using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Systems;

public sealed partial class WorldOriginRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    public IRenderExtractionCollector CreateRangeCollector() => new WorldOriginRenderExtractor();
    public void Dispose() => Reset();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (WorldOriginRenderExtractor)collector;
        _origins.AddRange(values._origins);
        _sources.UnionWith(values._sources);
        return values._origins.Count != 0;
    }
}
