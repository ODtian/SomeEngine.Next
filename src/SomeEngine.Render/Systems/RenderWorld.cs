using SomeEngine.ECS;
using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Render.Systems;

internal readonly record struct RenderMaterialIndex(ImmutableDictionary<long, Entity> Entities) : IComponent;

/// <summary>
/// Independent render ECS. It contains extracted snapshots as well as render-only entities and
/// pipeline-owned components. Extraction and pipeline scheduling are deliberately owned by
/// separate system groups; this type is only their ECS storage.
/// </summary>
public sealed class RenderWorld : World
{
    private readonly Entity _materialIndexEntity;
    private readonly QueryHandle _worldOriginQuery;

    public RenderWorld(int initialEntityCapacity = 256, WorldExecution? execution = null)
        : base(initialEntityCapacity, execution)
    {
        _materialIndexEntity = CreateEntity(new RenderMaterialIndex(ImmutableDictionary<long, Entity>.Empty));
        _worldOriginQuery = Query(new QueryDefinitionBuilder().Read<RenderWorldOrigin>());
    }

    /// <summary>The shared origin extracted for this world; coordinates use engine world units.</summary>
    public WorldOrigin WorldOrigin
    {
        get
        {
            WorldOrigin origin = default;
            int count = 0;
            ExecuteQuery(_worldOriginQuery, query =>
            {
                foreach (QueryChunkView chunk in query.Chunks)
                    foreach (RenderWorldOrigin value in chunk.Read<RenderWorldOrigin>())
                    {
                        SomeEngine.Core.Diagnostics.DevelopmentCheck.Require(++count == 1,
                            "A render world has one shared coordinate origin.");
                        origin = value.Value;
                    }
            });
            return origin;
        }
    }

    /// <summary>Reads the shared material referenced through this world's extraction boundary.</summary>
    public Material ReadMaterial(long materialId) =>
        Read<RenderMaterialReference>(Read<RenderMaterialIndex>(_materialIndexEntity).Entities[materialId]).Material;

    internal bool TryReadMaterial(long materialId, out Material? data)
    {
        if (Read<RenderMaterialIndex>(_materialIndexEntity).Entities.TryGetValue(materialId, out Entity entity))
        {
            data = Read<RenderMaterialReference>(entity).Material;
            return true;
        }
        data = null;
        return false;
    }

    internal void UpdateMaterials(IEnumerable<Material> values, IEnumerable<long> removed)
    {
        var before = Read<RenderMaterialIndex>(_materialIndexEntity).Entities;
        var entities = before;
        foreach (long id in removed)
            if (entities.TryGetValue(id, out Entity entity))
            {
                DestroyEntity(entity);
                entities = entities.Remove(id);
            }
        foreach (Material data in values)
        {
            if (entities.TryGetValue(data.MaterialId, out Entity entity))
            {
                if (!ReferenceEquals(Read<RenderMaterialReference>(entity).Material, data))
                    Replace(entity, new RenderMaterialReference(data));
            }
            else
                entities = entities.Add(data.MaterialId, CreateEntity(new RenderMaterialReference(data)));
        }
        if (!ReferenceEquals(before, entities)) Replace(_materialIndexEntity, new RenderMaterialIndex(entities));
    }

    internal void ApplyExtraction(RenderExtractionSystems extraction)
    {
        ExecuteStructuralTransaction(
            extraction,
            static systems => systems.ApplyCandidate());
    }

    internal void ApplyExtractionChanges(RenderExtractionSystems extraction)
    {
        ExecuteStructuralTransaction(
            extraction,
            static systems => systems.ApplyChangesCandidate());
    }

    internal bool TryApplyExtractionChangesDirect(RenderExtractionSystems extraction) =>
        ExecuteValueMutation(
            extraction.DestinationAccess,
            extraction,
            static systems => systems.TryApplyChangesDirect());

    internal bool HasExtractionValueHooks(ReadOnlySpan<int> components)
    {
        foreach (int component in components)
            if (HasValueReplaceHookCallbacks(component)) return true;
        return false;
    }
}
