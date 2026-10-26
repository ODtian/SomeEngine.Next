using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Systems;

public sealed partial class MaterialDecalRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    private readonly SourceTransformChanges _transformChanges = new();

    public IRenderExtractionCollector CreateRangeCollector() => new MaterialDecalRenderExtractor();
    public void Dispose() => Reset();

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (MaterialDecalRenderExtractor)collector;
        _decals.AddRange(values._decals);
        _sources.UnionWith(values._sources);
        values._transformChanges.Merge(context);
        return values._decals.Count != 0;
    }
}
