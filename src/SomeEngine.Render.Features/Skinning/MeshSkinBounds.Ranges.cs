using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinBoundsRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    public IRenderExtractionCollector CreateRangeCollector() => new SkinBoundsRenderExtractor();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (SkinBoundsRenderExtractor)collector;
        _values.AddRange(values._values);
        _sources.UnionWith(values._sources);
        return values._values.Count != 0;
    }
}
