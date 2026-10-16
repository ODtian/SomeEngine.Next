using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Transfers decal references and transforms through the shared extraction boundary.</summary>
public sealed class MaterialDecalRenderExtractor : IRenderExtractionSystem
{
    private readonly List<(Entity Source, RenderTransform Transform, RenderMaterialDecal Decal)> _decals = [];
    private readonly HashSet<Entity> _sources = [];
    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMaterialDecal>();
        query.ReadOptional<MaterialDecal>();
        query.ReadOptional<WorldTransform>();
    }

    public void Reset() { _decals.Clear(); _sources.Clear(); }

    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<MaterialDecal>(out var decals) || !chunk.TryRead<WorldTransform>(out var transforms)) return;
        for (int row = 0; row < decals.Length; row++)
        {
            Entity source = chunk.Entities[row];
            _sources.Add(source);
            _decals.Add((source, new(transforms[row].Qvvs), new(decals[row].Material, decals[row].Order)));
        }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.TryRead<MaterialDecal>(out var decals) || !chunk.TryRead<WorldTransform>(out var transforms)) return false;
        int before = _decals.Count;
        for (int row = 0; row < decals.Length; row++)
        {
            Entity source = chunk.Entities[row];
            if (chunk.RowChangedSinceLastSystemVersion<WorldTransform>(row))
                context.CollectTransformChange(source, new RenderTransform(transforms[row].Qvvs));
            if (chunk.RowChangedSinceLastSystemVersion<MaterialDecal>(row))
                _decals.Add((source, default, new(decals[row].Material, decals[row].Order)));
        }
        return _decals.Count != before;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (var value in _decals)
        {
            Entity entity = context.RetainMirror(value.Source);
            context.RetainTransform(entity, value.Transform);
            context.Upsert(entity, value.Decal);
        }
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.Contains(mirror.Source)) context.RemoveIfExists<RenderMaterialDecal>(mirror.RenderEntity);
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach (var value in _decals) context.UpdateExisting(context.RequireMirror(value.Source), value.Decal);
    }
}
