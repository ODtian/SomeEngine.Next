using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

internal readonly record struct RenderTerrainChunk(object Scene, int GeometryId, ulong Revision) : IComponent;
internal readonly record struct RenderTerrainSelection(object Scene, int[] GeometryIds) : IComponent;

internal sealed class TerrainGeometryExtractor(object scene) : IRenderExtractionSystem
{
    private readonly List<ChunkData> _chunks = [];
    private (Entity Entity, TerrainGpuSelection Value)? _selection;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<TerrainGpuSource>();
        query.ReadOptional<TerrainGpuSelection>();
        query.ReadOptional<TerrainChunk>();
        query.ReadOptional<WorldTransform>();
        query.ReadOptionalBuffer<MeshMaterialBinding>();
        query.Write<RenderTerrainChunk>();
        query.Write<RenderTerrainSelection>();
        query.Write<RenderMaterialReference>();
    }

    public void Reset()
    {
        _chunks.Clear();
        _selection = null;
    }

    public void Collect(QueryChunkView chunk)
    {
        if (chunk.TryRead<TerrainGpuSelection>(out var selections))
            for (int row = 0; row < selections.Length; row++)
                if (ReferenceEquals(selections[row].Scene, scene)) _selection = (chunk.Entities[row], selections[row]);
        if (!chunk.TryRead<TerrainGpuSource>(out var sources)) return;
        var values = chunk.Read<TerrainChunk>();
        var transforms = chunk.Read<WorldTransform>();
        for (int row = 0; row < sources.Length; row++)
        {
            if (!ReferenceEquals(sources[row].Scene, scene)) continue;
            var bindings = chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
            var materials = new Material[bindings.Length];
            for (int index = 0; index < materials.Length; index++) materials[index] = bindings[index].Material;
            _chunks.Add(new(chunk.Entities[row], values[row], new RenderTransform(transforms[row].Qvvs), materials));
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (chunk.Has<TerrainGpuSelection>() && chunk.HasChangedSinceLastSystemVersion<TerrainGpuSelection>())
        {
            Collect(chunk);
            return true;
        }
        if (!chunk.Has<TerrainGpuSource>()) return false;
        if (chunk.HasChangedSinceLastSystemVersion<WorldTransform>()) context.CollectTransformChanges(chunk);
        if (!chunk.HasChangedSinceLastSystemVersion<TerrainChunk>()
            && !chunk.HasChangedSinceLastSystemVersion<WorldTransform>()
            && !chunk.HasBufferChangedSinceLastSystemVersion<MeshMaterialBinding>()) return false;
        Collect(chunk);
        return true;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (ChunkData chunk in _chunks)
            context.RetainTransform(context.RetainMirror(chunk.Entity), chunk.Transform);
        ApplyChunks(context);
    }
    public void ApplyChanges(RenderExtractionContext context) => ApplyChunks(context);

    private void ApplyChunks(RenderExtractionContext context)
    {
        if (_selection is { } selection)
            context.Upsert(context.RetainMirror(selection.Entity), new RenderTerrainSelection(scene, selection.Value.GeometryIds));
        foreach (ChunkData chunk in _chunks)
        {
            Entity entity = context.RetainMirror(chunk.Entity);
            context.Upsert(entity, new RenderTerrainChunk(scene, chunk.Chunk.GeometryId, chunk.Chunk.Revision));
            context.Upsert(entity, new RenderInstance());
            context.SetMaterials(entity, chunk.Materials);
        }
    }

    private sealed record ChunkData(Entity Entity, TerrainChunk Chunk, RenderTransform Transform, Material[] Materials);
}
