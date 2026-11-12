using SomeEngine.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Transfers decal references and transforms through the shared extraction boundary.</summary>
public sealed partial class MaterialDecalRenderExtractor : IRenderExtractionSystem
{
    private readonly List<(Entity Source, RenderTransform? Transform, RenderMaterialDecal Decal)> _decals = [];
    private readonly HashSet<Entity> _sources = [];
    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderMaterialDecal>();
        query.ReadOptional<MaterialDecal>();
        query.ReadOptional<WorldTransform>();
    }

    public void Reset() { _decals.Clear(); _sources.Clear(); _transformChanges.Clear(); }

    public void CollectResources(scoped QueryScope source, RenderExtractionResources resources)
    {
        foreach (QueryChunk chunk in source)
            if (chunk.TryRead<MaterialDecal>(out var decals))
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Has<MaterialDecal>(row)) resources.Add(decals[row].Material);
    }

    public void Collect(QueryChunk chunk)
    {
        if (!chunk.TryRead<MaterialDecal>(out var decals) || !chunk.TryRead<WorldTransform>(out var transforms)) return;
        for (int row = 0; row < decals.Length; row++)
        {
            if (!chunk.Has<MaterialDecal>(row) || !chunk.Has<WorldTransform>(row)) continue;
            Entity source = chunk.Entities[row];
            _sources.Add(source);
            _decals.Add((source, new RenderTransform(transforms[row].Qvvs), new(decals[row].Material, decals[row].Order)));
        }
    }

    public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context)
    {
        bool changed = CollectChanges(chunk, since);
        _transformChanges.Merge(context);
        return changed;
    }

    private bool CollectChanges(QueryChunk chunk, ulong lastSystemVersion)
    {
        if (!chunk.TryRead<MaterialDecal>(out var decals) || !chunk.TryRead<WorldTransform>(out var transforms)) return false;
        int before = _decals.Count;
        for (int row = 0; row < decals.Length; row++)
        {
            if (!chunk.Has<MaterialDecal>(row) || !chunk.Has<WorldTransform>(row)) continue;
            Entity source = chunk.Entities[row];
            if (chunk.WasWritten<WorldTransform>(row, lastSystemVersion))
                _transformChanges.Add(source, new RenderTransform(transforms[row].Qvvs));
            if (chunk.WasWritten<MaterialDecal>(row, lastSystemVersion))
                _decals.Add((source, default, new(decals[row].Material, decals[row].Order)));
        }
        return _decals.Count != before;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (var value in _decals)
        {
            Entity entity = context.RetainMirror(value.Source);
            if (value.Transform is { } transform) context.RetainTransform(entity, transform);
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
