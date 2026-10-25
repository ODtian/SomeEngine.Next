using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Systems;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>LBS influence boxes retained by a mesh instance.</summary>
public readonly record struct MeshSkinBounds(ClusterSkinBounds Data) : IComponent;

internal readonly record struct RenderSkinBounds(ClusterSkinBounds Data) : IComponent;

/// <summary>Copies ready influence data through normal Game-to-Rendering extraction.</summary>
public sealed partial class SkinBoundsRenderExtractor : IRenderExtractionSystem
{
    private readonly List<(Entity Source, ClusterSkinBounds Data)> _values = [];
    private readonly HashSet<Entity> _sources = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<MeshSkinBounds>();
        query.Write<RenderSkinBounds>();
        query.Write<RenderDeformBounds>();
    }

    public void Reset() { _values.Clear(); _sources.Clear(); }

    public void Collect(QueryChunkView chunk) => Collect(ReadOnlyQueryPacketJobs.Borrow(chunk));

    private void Collect(ReadOnlyQueryPacket chunk)
    {
        if (!chunk.TryRead<MeshSkinBounds>(out var values)) return;
        for (int row = 0; row < values.Length; row++)
        {
            _values.Add((chunk.Entities[row], values[row].Data));
            _sources.Add(chunk.Entities[row]);
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) =>
        CollectChanges(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion);

    private bool CollectChanges(ReadOnlyQueryPacket chunk, uint lastSystemVersion)
    {
        if (!chunk.TryRead<MeshSkinBounds>(out var values) || !chunk.ChangedSince<MeshSkinBounds>(lastSystemVersion)) return false;
        int before = _values.Count;
        for (int row = 0; row < values.Length; row++)
            if (chunk.RowChangedSince<MeshSkinBounds>(row, lastSystemVersion)) _values.Add((chunk.Entities[row], values[row].Data));
        return _values.Count != before;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (var value in _values) Store(context, context.RetainMirror(value.Source), value.Data);
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.Contains(mirror.Source)) context.RemoveIfExists<RenderSkinBounds>(mirror.RenderEntity);
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach (var value in _values) Store(context, context.RequireMirror(value.Source), value.Data);
    }

    private static void Store(RenderExtractionContext context, Entity entity, ClusterSkinBounds data)
    {
        context.Upsert(entity, new RenderSkinBounds(data));
        if (!context.World.Has<RenderDeformBounds>(entity)) context.Upsert(entity, new RenderDeformBounds());
    }

    public void Dispose() { _values.Clear(); _sources.Clear(); }
}
