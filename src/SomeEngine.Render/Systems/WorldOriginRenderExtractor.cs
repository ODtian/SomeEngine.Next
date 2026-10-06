using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts the Game world's shared coordinate origin.</summary>
public sealed class WorldOriginRenderExtractor : IRenderExtractionSystem
{
    private readonly List<(Entity Source, WorldOrigin Origin)> _origins = [];
    private readonly HashSet<Entity> _sources = [];

    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<WorldOrigin>();
        query.Write<RenderWorldOrigin>();
    }

    public void Reset() { _origins.Clear(); _sources.Clear(); }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<WorldOrigin>(out ReadOnlySpan<WorldOrigin> origins)) return;
        for (int row = 0; row < chunk.Count; row++)
        {
            _origins.Add((chunk.Entities[row], origins[row]));
            _sources.Add(chunk.Entities[row]);
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.TryRead<WorldOrigin>(out ReadOnlySpan<WorldOrigin> origins) ||
            !chunk.HasChangedSinceLastSystemVersion<WorldOrigin>()) return false;
        for (int row = 0; row < chunk.Count; row++)
            if (chunk.RowChangedSinceLastSystemVersion<WorldOrigin>(row))
                _origins.Add((chunk.Entities[row], origins[row]));
        return _origins.Count != 0;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (var origin in _origins)
            context.Upsert(context.RetainMirror(origin.Source), new RenderWorldOrigin(origin.Origin));
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.Contains(mirror.Source)) context.RemoveIfExists<RenderWorldOrigin>(mirror.RenderEntity);
    }

    public void ValidateChanges(RenderExtractionContext context)
    {
        foreach (var origin in _origins)
            context.RequireExisting<RenderWorldOrigin>(context.RequireMirror(origin.Source));
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach (var origin in _origins)
            context.UpdateExisting(context.RequireMirror(origin.Source), new RenderWorldOrigin(origin.Origin));
    }
}
