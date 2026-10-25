using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

internal readonly record struct RenderTerrainChunk(object Scene, int GeometryId, ulong Revision) : IComponent;
internal readonly record struct RenderTerrainSelection(object Scene, int[] GeometryIds) : IComponent;

internal sealed partial class TerrainGeometryExtractor(object scene) : IRenderExtractionSystem
{
    private readonly List<ChunkData> _chunks = [];
    private readonly SourceTransformChanges _transformChanges = new();
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
        _transformChanges.Clear();
        _selection = null;
    }

    public void Collect(QueryChunkView chunk) => Collect(ReadOnlyQueryPacketJobs.Borrow(chunk));

    private void Collect(ReadOnlyQueryPacket chunk)
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
        bool changed = CollectChanges(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion);
        _transformChanges.Merge(context);
        return changed;
    }

    private bool CollectChanges(ReadOnlyQueryPacket chunk, uint lastSystemVersion)
    {
        if (chunk.Has<TerrainGpuSelection>() && chunk.ChangedSince<TerrainGpuSelection>(lastSystemVersion))
        {
            Collect(chunk);
            return true;
        }
        if (!chunk.Has<TerrainGpuSource>()) return false;
        if (chunk.ChangedSince<WorldTransform>(lastSystemVersion)) _transformChanges.Collect(chunk, lastSystemVersion);
        if (!chunk.ChangedSince<TerrainChunk>(lastSystemVersion)
            && !chunk.ChangedSince<WorldTransform>(lastSystemVersion)
            && !chunk.BufferChangedSince<MeshMaterialBinding>(lastSystemVersion)) return false;
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
