using SomeEngine.ECS;
using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Systems;

internal readonly record struct RenderMaterialEntry(Entity Entity, int ReferenceCount);
internal readonly record struct RenderMaterialIndex(ImmutableDictionary<long, RenderMaterialEntry> Entries) : IComponent;

/// <summary>
/// Independent render ECS. It contains extracted snapshots as well as render-only entities and
/// pipeline-owned components. Extraction and pipeline scheduling are deliberately owned by
/// separate system groups; this type is only their ECS storage.
/// </summary>
public sealed class RenderWorld : World
{
    private readonly Entity _materialIndexEntity;

    public RenderWorld(int initialEntityCapacity = 256, WorldExecution? execution = null)
        : base(initialEntityCapacity, execution)
    {
        _materialIndexEntity = CreateEntity(new RenderMaterialIndex(ImmutableDictionary<long, RenderMaterialEntry>.Empty));
    }

    /// <summary>Reads the shared material referenced through this world's extraction boundary.</summary>
    public Material ReadMaterial(long materialId) =>
        Read<RenderMaterialReference>(Read<RenderMaterialIndex>(_materialIndexEntity).Entries[materialId].Entity).Material;

    internal bool TryReadMaterial(long materialId, out Material? data)
    {
        if (Read<RenderMaterialIndex>(_materialIndexEntity).Entries.TryGetValue(materialId, out RenderMaterialEntry entry))
        {
            data = Read<RenderMaterialReference>(entry.Entity).Material;
            return true;
        }
        data = null;
        return false;
    }

    internal void SetMaterials(Entity entity, ReadOnlySpan<Material> materials)
    {
        long[] previous = [];
        if (HasBuffer<RenderMaterialBinding>(entity))
            ExecuteBufferRead<RenderMaterialBinding, long[]>(entity, ref previous,
                static (BufferView<RenderMaterialBinding> values, ref long[] result) =>
                {
                    ReadOnlySpan<RenderMaterialBinding> bindings = values.AsSpan();
                    result = new long[bindings.Length];
                    for (int index = 0; index < result.Length; index++) result[index] = bindings[index].MaterialId;
                });
        else AddBuffer<RenderMaterialBinding>(entity);
        bool changed = previous.Length != materials.Length;
        for (int index = 0; !changed && index < previous.Length; index++)
            changed = previous[index] != materials[index].MaterialId;
        if (!changed) return;

        var entries = Read<RenderMaterialIndex>(_materialIndexEntity).Entries;
        foreach (long id in previous)
            entries = entries.SetItem(id, entries[id] with { ReferenceCount = entries[id].ReferenceCount - 1 });
        var ids = new long[materials.Length];
        for (int index = 0; index < materials.Length; index++)
        {
            Material material = materials[index];
            long id = ids[index] = material.MaterialId;
            if (!entries.TryGetValue(id, out RenderMaterialEntry entry))
                entry = new(CreateEntity(new RenderMaterialReference(material)), 0);
            entries = entries.SetItem(id, entry with { ReferenceCount = entry.ReferenceCount + 1 });
        }
        foreach (long id in previous)
            if (entries.TryGetValue(id, out RenderMaterialEntry entry) && entry.ReferenceCount == 0)
            {
                DestroyEntity(entry.Entity);
                entries = entries.Remove(id);
            }
        Replace(_materialIndexEntity, new RenderMaterialIndex(entries));
        ExecuteBufferWrite<RenderMaterialBinding, long[]>(entity, ref ids,
            static (DynamicBuffer<RenderMaterialBinding> values, ref long[] data) =>
            {
                values.Clear();
                values.EnsureCapacity(data.Length);
                foreach (long id in data) values.Add(new(id));
            });
    }

    internal void RemoveMaterials(Entity entity)
    {
        if (!HasBuffer<RenderMaterialBinding>(entity)) return;
        SetMaterials(entity, []);
        RemoveBuffer<RenderMaterialBinding>(entity);
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
