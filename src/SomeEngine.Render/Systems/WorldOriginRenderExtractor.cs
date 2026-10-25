using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts the Game world's shared coordinate origin.</summary>
public sealed partial class WorldOriginRenderExtractor : IRenderExtractionSystem
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

    public void Collect(QueryChunkView chunk) => Collect(ReadOnlyQueryPacketJobs.Borrow(chunk));

    private void Collect(ReadOnlyQueryPacket chunk)
    {
        if (!chunk.TryRead<WorldOrigin>(out ReadOnlySpan<WorldOrigin> origins)) return;
        for (int row = 0; row < chunk.Count; row++)
        {
            _origins.Add((chunk.Entities[row], origins[row]));
            _sources.Add(chunk.Entities[row]);
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) =>
        CollectChanges(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion);

    private bool CollectChanges(ReadOnlyQueryPacket chunk, uint lastSystemVersion)
    {
        if (!chunk.TryRead<WorldOrigin>(out ReadOnlySpan<WorldOrigin> origins) ||
            !chunk.ChangedSince<WorldOrigin>(lastSystemVersion)) return false;
        for (int row = 0; row < chunk.Count; row++)
            if (chunk.RowChangedSince<WorldOrigin>(row, lastSystemVersion))
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
