using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Terrains;

internal sealed partial class TerrainGeometryExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    public IRenderExtractionCollector CreateRangeCollector() => new TerrainGeometryExtractor(scene);
    public void Dispose() => Reset();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (TerrainGeometryExtractor)collector;
        _chunks.AddRange(values._chunks);
        if (values._selection is not null) _selection = values._selection;
        values._transformChanges.Merge(context);
        return values._chunks.Count != 0 || values._selection is not null;
    }
}
