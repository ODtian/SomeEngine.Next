using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Systems;

internal readonly record struct RenderTextureVersions(ImmutableDictionary<Texture, TextureVersions> Textures) : IComponent, ITrackedComponent;

internal readonly record struct RenderMaterialEntry(Entity Entity, int ReferenceCount);
internal readonly record struct RenderMaterialIndex(ImmutableDictionary<long, RenderMaterialEntry> Entries) : IComponent;

/// <summary>
/// Independent render ECS. It contains extracted snapshots as well as render-only entities and
/// pipeline-owned components. Extraction and pipeline scheduling are deliberately owned by
/// separate system groups; this type is only their ECS storage.
/// </summary>
public sealed class RenderWorld : IDisposable
{
    private readonly Entity _materialIndexEntity;
    private readonly Entity _textureVersionsEntity;
    private readonly Query _worldOriginQuery;
    public World World { get; }

    public RenderWorld(int initialEntityCapacity = 256, WorldExecution? execution = null)
    {
        World = new World(initialEntityCapacity, execution: execution);
        _worldOriginQuery = World.Query(new QueryDescription().With<RenderWorldOrigin>());
        _materialIndexEntity = World.Create(new RenderMaterialIndex(ImmutableDictionary<long, RenderMaterialEntry>.Empty));
        _textureVersionsEntity = World.Create(new RenderTextureVersions(ImmutableDictionary<Texture, TextureVersions>.Empty));
    }

    internal static bool IsRenderingWorld(World world) =>
        world.Query<RenderMaterialIndex>().Count != 0 && world.Query<RenderTextureVersions>().Count != 0;

    /// <summary>The shared origin extracted for this world; coordinates use engine world units.</summary>
    public WorldOrigin WorldOrigin
    {
        get
        {
            WorldOrigin origin = default;
            int count = 0;
            using QueryScope query = _worldOriginQuery.OpenRead();
            foreach (QueryChunk chunk in query)
                foreach (RenderWorldOrigin value in chunk.Read<RenderWorldOrigin>())
                {
                    SomeEngine.Core.Diagnostics.DevelopmentCheck.Require(++count == 1,
                        "A render world has one shared coordinate origin.");
                    origin = value.Value;
                }
            return origin;
        }
    }

    /// <summary>Reads the shared material referenced through this world's extraction boundary.</summary>
    public Material ReadMaterial(long materialId) =>
        World.ReadValue<RenderMaterialReference>(World.ReadValue<RenderMaterialIndex>(_materialIndexEntity).Entries[materialId].Entity).Material;

    public ulong ReadMaterialRevision(long materialId) => ReadMaterialReference(materialId).Revision;
    internal RenderMaterialReference ReadMaterialReference(long materialId) =>
        World.ReadValue<RenderMaterialReference>(World.ReadValue<RenderMaterialIndex>(_materialIndexEntity).Entries[materialId].Entity);
    public TextureVersions ReadTextureVersions(Texture texture) => TextureVersions[texture];
    internal ImmutableDictionary<Texture, TextureVersions> TextureVersions =>
        World.ReadValue<RenderTextureVersions>(_textureVersionsEntity).Textures;
    internal void UpdateTextureVersions(ImmutableDictionary<Texture, TextureVersions> textures) => UpdateTextureVersions(World, textures);
    internal void UpdateTextureVersions(World target, ImmutableDictionary<Texture, TextureVersions> textures) =>
        target.Set(_textureVersionsEntity, new RenderTextureVersions(textures));
    internal void UpdateMaterials(IEnumerable<RenderMaterialReference> references) => UpdateMaterials(World, references);
    internal void UpdateMaterials(World target, IEnumerable<RenderMaterialReference> references)
    {
        var entries = target.ReadValue<RenderMaterialIndex>(_materialIndexEntity).Entries;
        foreach (RenderMaterialReference reference in references)
            if (entries.TryGetValue(reference.Material.MaterialId, out RenderMaterialEntry entry) &&
                target.ReadValue<RenderMaterialReference>(entry.Entity) != reference)
                target.Set(entry.Entity, reference);
    }

    internal bool TryReadMaterial(long materialId, out Material? data)
    {
        if (World.ReadValue<RenderMaterialIndex>(_materialIndexEntity).Entries.TryGetValue(materialId, out RenderMaterialEntry entry))
        {
            data = World.ReadValue<RenderMaterialReference>(entry.Entity).Material;
            return true;
        }
        data = null;
        return false;
    }

    internal void SetMaterials(World target, Entity entity, ReadOnlySpan<Material> materials,
        IReadOnlyDictionary<long, RenderMaterialReference> references)
    {
        using WorldStructureWriteScope structure = target.OpenStructureWrite();
        long[] previous = [];
        if (target.Has<Buffer<RenderMaterialBinding>>(entity))
        {
            using ReadOnlyBuffer<RenderMaterialBinding> values = target.ReadBuffer<RenderMaterialBinding>(entity);
            ReadOnlySpan<RenderMaterialBinding> priorBindings = values.AsSpan();
            previous = new long[priorBindings.Length];
            for (int index = 0; index < previous.Length; index++) previous[index] = priorBindings[index].MaterialId;
        }
        else target.AddBuffer<RenderMaterialBinding>(entity);
        bool changed = previous.Length != materials.Length;
        for (int index = 0; !changed && index < previous.Length; index++) changed = previous[index] != materials[index].MaterialId;
        if (!changed) return;
        var entries = target.ReadValue<RenderMaterialIndex>(_materialIndexEntity).Entries;
        foreach (long id in previous) entries = entries.SetItem(id, entries[id] with { ReferenceCount = entries[id].ReferenceCount - 1 });
        var ids = new long[materials.Length];
        for (int index = 0; index < materials.Length; index++)
        {
            long id = ids[index] = materials[index].MaterialId;
            if (!entries.TryGetValue(id, out RenderMaterialEntry entry)) entry = new(target.Create(references[id]), 0);
            entries = entries.SetItem(id, entry with { ReferenceCount = entry.ReferenceCount + 1 });
        }
        foreach (long id in previous)
            if (entries.TryGetValue(id, out RenderMaterialEntry entry) && entry.ReferenceCount == 0)
            {
                target.Destroy(entry.Entity);
                entries = entries.Remove(id);
            }
        target.Set(_materialIndexEntity, new RenderMaterialIndex(entries));
        using DynamicBuffer<RenderMaterialBinding> bindings = target.GetBuffer<RenderMaterialBinding>(entity);
        bindings.Clear(); bindings.EnsureCapacity(ids.Length);
        foreach (long id in ids) bindings.Add(new(id));
    }

    internal void SetMaterials(Entity entity, ReadOnlySpan<Material> materials,
        IReadOnlyDictionary<long, RenderMaterialReference> references) => SetMaterials(World, entity, materials, references);

    internal void RemoveMaterials(World target, Entity entity)
    {
        if (!target.Has<Buffer<RenderMaterialBinding>>(entity)) return;
        SetMaterials(target, entity, [], ImmutableDictionary<long, RenderMaterialReference>.Empty);
        target.RemoveBuffer<RenderMaterialBinding>(entity);
    }

    internal void RemoveMaterials(Entity entity) => RemoveMaterials(World, entity);

    internal void ApplyExtraction(RenderExtractionSystems extraction) => ApplyCandidate(extraction, complete: true);
    internal void ApplyExtractionChanges(RenderExtractionSystems extraction) => ApplyCandidate(extraction, complete: false);

    private void ApplyCandidate(RenderExtractionSystems extraction, bool complete)
    {
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(World, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        extraction.SelectTarget(candidate);
        try
        {
            using (WorldStructureWriteScope structure = candidate.OpenStructureWrite())
                if (complete) extraction.ApplyCandidate();
                else extraction.ApplyChangesCandidate();
            checkpoint.Capture(candidate, CheckpointProgress.Preserve);
            checkpoint.Restore(World);
        }
        finally { extraction.SelectTarget(World); }
    }

    internal bool TryApplyExtractionChangesDirect(RenderExtractionSystems extraction)
    {
        using WorldStructureWriteScope structure = World.OpenStructureWrite();
        using QueryScope values = World.Query(extraction.DestinationAccess).Open();
        return extraction.TryApplyChangesDirect();
    }

    public void Dispose() => World.Dispose();
}
