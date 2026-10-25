using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Systems;

public sealed partial class MeshRenderExtractor : IRenderExtractionRangeSystem, IRenderExtractionCollector
{
    private readonly MeshRenderExtractor? _source;
    private readonly SourceTransformChanges _transformChanges = new();

    public MeshRenderExtractor() { }
    private MeshRenderExtractor(MeshRenderExtractor source) => _source = source;

    public IRenderExtractionCollector CreateRangeCollector() => new MeshRenderExtractor(this);

    void IRenderExtractionCollector.Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
    {
        if (fullCollection) Collect(packet);
        else CollectChanges(packet, lastSystemVersion);
    }

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (MeshRenderExtractor)collector;
        int materialOffset = _materials.Count;
        _materials.AddRange(values._materials);
        foreach (MeshSnapshot value in values._snapshots)
            _snapshots.Add(value with { MaterialOffset = materialOffset + value.MaterialOffset });
        _meshSources.UnionWith(values._meshSources);
        _collectedMeshSources.UnionWith(values._collectedMeshSources);
        foreach (var value in values._sourceMeshes) _sourceMeshes[value.Key] = value.Value;
        foreach (var value in values._sourceMaterials) _sourceMaterials[value.Key] = value.Value;
        values._transformChanges.Merge(context);
        return values._snapshots.Count != 0;
    }
}
