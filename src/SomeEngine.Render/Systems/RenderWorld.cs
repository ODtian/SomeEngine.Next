using SomeEngine.ECS;
using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Systems;

internal readonly record struct RenderMaterialIndex(ImmutableDictionary<long, Entity> Entities) : IComponent;
internal readonly record struct RenderTextureVersions(ImmutableDictionary<Texture, TextureVersions> Textures) : IComponent;

/// <summary>
/// Independent render ECS. It contains extracted snapshots as well as render-only entities and
/// pipeline-owned components. Extraction and pipeline scheduling are deliberately owned by
/// separate system groups; this type is only their ECS storage.
/// </summary>
public sealed class RenderWorld : World
{
    private readonly Entity _materialIndexEntity;
    private readonly Entity _textureVersionsEntity;

    public RenderWorld(int initialEntityCapacity = 256, WorldExecution? execution = null)
        : base(initialEntityCapacity, execution)
    {
        _materialIndexEntity = CreateEntity(new RenderMaterialIndex(ImmutableDictionary<long, Entity>.Empty));
        _textureVersionsEntity = CreateEntity(new RenderTextureVersions(ImmutableDictionary<Texture, TextureVersions>.Empty));
    }

    /// <summary>Reads the shared material referenced through this world's extraction boundary.</summary>
    public Material ReadMaterial(long materialId) =>
        Read<RenderMaterialReference>(Read<RenderMaterialIndex>(_materialIndexEntity).Entities[materialId]).Material;
    public ulong ReadMaterialRevision(long materialId) => ReadMaterialReference(materialId).Revision;
    internal RenderMaterialReference ReadMaterialReference(long materialId) =>
        Read<RenderMaterialReference>(Read<RenderMaterialIndex>(_materialIndexEntity).Entities[materialId]);

    public TextureVersions ReadTextureVersions(Texture texture) =>
        Read<RenderTextureVersions>(_textureVersionsEntity).Textures[texture];
    internal ImmutableDictionary<Texture, TextureVersions> TextureVersions =>
        Read<RenderTextureVersions>(_textureVersionsEntity).Textures;
    internal void UpdateTextureVersions(ImmutableDictionary<Texture, TextureVersions> textures) =>
        Replace(_textureVersionsEntity, new RenderTextureVersions(textures));

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

    internal void UpdateMaterials(IEnumerable<RenderMaterialReference> values, IEnumerable<long> removed)
    {
        var before = Read<RenderMaterialIndex>(_materialIndexEntity).Entities;
        var entities = before;
        foreach (long id in removed)
            if (entities.TryGetValue(id, out Entity entity))
            {
                DestroyEntity(entity);
                entities = entities.Remove(id);
            }
        foreach (RenderMaterialReference data in values)
        {
            if (entities.TryGetValue(data.Material.MaterialId, out Entity entity))
            {
                if (Read<RenderMaterialReference>(entity) != data) Replace(entity, data);
            }
            else
                entities = entities.Add(data.Material.MaterialId, CreateEntity(data));
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
