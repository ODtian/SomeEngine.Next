using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Copies mesh X-ray rules through the existing Game-to-render extraction boundary.</summary>
public sealed class XrayRenderExtractor : IRenderExtractionSystem
{
    private readonly List<(Entity Source, RenderXray Effect)> _effects = [];
    private readonly HashSet<Entity> _sources = [];

    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<Xray>();
        query.Write<RenderXray>();
    }

    public void Reset() { _effects.Clear(); _sources.Clear(); }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<Xray>(out ReadOnlySpan<Xray> effects)) return;
        for (int row = 0; row < chunk.Count; row++)
        {
            _effects.Add((chunk.Entities[row], new(effects[row])));
            _sources.Add(chunk.Entities[row]);
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.TryRead<Xray>(out ReadOnlySpan<Xray> effects) ||
            !chunk.HasChangedSinceLastSystemVersion<Xray>()) return false;
        int count = _effects.Count;
        for (int row = 0; row < chunk.Count; row++)
            if (chunk.RowChangedSinceLastSystemVersion<Xray>(row))
                _effects.Add((chunk.Entities[row], new(effects[row])));
        return _effects.Count != count;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (var effect in _effects)
            context.Upsert(context.RetainMirror(effect.Source), effect.Effect);
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.Contains(mirror.Source)) context.RemoveIfExists<RenderXray>(mirror.RenderEntity);
    }

    public void ValidateChanges(RenderExtractionContext context)
    {
        foreach (var effect in _effects)
            context.RequireExisting<RenderXray>(context.RequireMirror(effect.Source));
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach (var effect in _effects)
            context.UpdateExisting(context.RequireMirror(effect.Source), effect.Effect);
    }
}
