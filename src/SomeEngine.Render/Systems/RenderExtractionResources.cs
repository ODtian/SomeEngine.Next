using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Systems;

/// <summary>Resource versions collected during one Game world extraction.</summary>
public sealed class RenderExtractionResources
{
    private readonly ImmutableDictionary<Texture, TextureVersions>.Builder _textures = ImmutableDictionary.CreateBuilder<Texture, TextureVersions>();
    private readonly ImmutableDictionary<long, RenderMaterialReference>.Builder _materials = ImmutableDictionary.CreateBuilder<long, RenderMaterialReference>();
    private readonly HashSet<Texture> _collectedTextures = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<long> _collectedMaterials = [];

    public void Add(Texture texture)
    {
        if (!_collectedTextures.Add(texture)) return;
        TextureVersions versions = texture.Versions;
        if (!_textures.TryGetValue(texture, out TextureVersions previous) || previous != versions)
            _textures[texture] = versions;
    }

    public void Add(Material material)
    {
        if (!_collectedMaterials.Add(material.MaterialId)) return;
        var reference = new RenderMaterialReference(material);
        if (!_materials.TryGetValue(material.MaterialId, out RenderMaterialReference previous) || previous != reference)
            _materials[material.MaterialId] = reference;
        for (uint property = 0; property < material.PropertyCount; property++)
            if (material.GetTexture(property) is { } texture) Add(texture);
    }

    internal void BeginCollection()
    {
        _collectedTextures.Clear();
        _collectedMaterials.Clear();
    }

    internal void CompleteCollection()
    {
        RemoveUncollected(_textures, _collectedTextures);
        RemoveUncollected(_materials, _collectedMaterials);
    }

    private static void RemoveUncollected<TKey, TValue>(ImmutableDictionary<TKey, TValue>.Builder values,
        HashSet<TKey> collected) where TKey : notnull
    {
        // Add has placed every collected key in the builder.
        if (values.Count == collected.Count) return;
        foreach (TKey key in values.Keys.ToArray())
            if (!collected.Contains(key)) values.Remove(key);
    }

    internal void Clear()
    {
        _textures.Clear();
        _materials.Clear();
        BeginCollection();
    }

    internal ImmutableDictionary<Texture, TextureVersions> Textures => _textures.ToImmutable();
    internal ImmutableDictionary<long, RenderMaterialReference> Materials => _materials.ToImmutable();
}

internal sealed class RenderSourceResources
{
    private readonly Dictionary<Entity, RenderSourceMaterials> _sourceMaterials = [];
    private readonly Dictionary<RenderSourceMaterial[], RenderSourceMaterials> _materialSets = new(new MaterialArrayComparison());
    private readonly Dictionary<Entity, Texture> _sourceCookies = [];
    private readonly Dictionary<Material, RenderSourceMaterial> _materialReferences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Texture, int> _cookieReferences = new(ReferenceEqualityComparer.Instance);
    private readonly RenderExtractionResources _resources = new();

    internal void BeginCollection(IEnumerable<Entity> changedSources, bool firstCollection)
    {
        // An unaccepted ECS version repeats the affected rows after a failed apply.
        // These bindings follow source collection; RenderWorld keeps the applied versions.
        if (firstCollection)
        {
            Clear();
            return;
        }

        foreach (Entity source in changedSources)
        {
            RemoveMaterials(source);
            RemoveCookie(source);
        }
    }

    internal void Collect(QueryChunkView chunk, bool fullCollection)
    {
        bool materialsChanged = chunk.HasBuffer<MeshMaterialBinding>() &&
            (fullCollection || chunk.HasBufferChangedSinceLastSystemVersion<MeshMaterialBinding>());
        bool cookiesChanged = chunk.Has<LightCookie>() &&
            (fullCollection || chunk.HasChangedSinceLastSystemVersion<LightCookie>());
        if (!materialsChanged && !cookiesChanged) return;

        ReadOnlySpan<Entity> entities = chunk.Entities;
        ReadOnlySpan<LightCookie> cookies = cookiesChanged ? chunk.Read<LightCookie>() : default;
        for (int row = 0; row < entities.Length; row++)
        {
            Entity source = entities[row];
            if (materialsChanged &&
                (fullCollection && !_sourceMaterials.ContainsKey(source) ||
                    chunk.RowBufferChangedSinceLastSystemVersion<MeshMaterialBinding>(row)))
            {
                RemoveMaterials(source);
                ReadOnlySpan<MeshMaterialBinding> bindings = chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan();
                if (!bindings.IsEmpty)
                {
                    var materials = new RenderSourceMaterial[bindings.Length];
                    for (int index = 0; index < bindings.Length; index++)
                    {
                        Material material = bindings[index].Material;
                        if (!_materialReferences.TryGetValue(material, out RenderSourceMaterial? reference))
                            _materialReferences.Add(material, reference = new RenderSourceMaterial(material));
                        materials[index] = reference;
                    }
                    if (!_materialSets.TryGetValue(materials, out RenderSourceMaterials? set))
                    {
                        set = new RenderSourceMaterials(materials);
                        _materialSets.Add(materials, set);
                        foreach (RenderSourceMaterial material in materials) material.ReferenceCount++;
                    }
                    set.SourceCount++;
                    _sourceMaterials.Add(source, set);
                }
            }
            if (cookiesChanged &&
                (fullCollection && !_sourceCookies.ContainsKey(source) ||
                    chunk.RowChangedSinceLastSystemVersion<LightCookie>(row)))
            {
                RemoveCookie(source);
                if (cookies[row].Texture is { } texture)
                {
                    _sourceCookies.Add(source, texture);
                    AddReference(_cookieReferences, texture);
                }
            }
        }
    }

    internal RenderExtractionResources CollectResources()
    {
        _resources.BeginCollection();
        foreach (RenderSourceMaterial reference in _materialReferences.Values)
        {
            reference.BoundsExpansion = reference.Material.BoundsExpansion;
            _resources.Add(reference.Material);
        }
        foreach (RenderSourceMaterials set in _materialSets.Values)
        {
            float expansion = 0;
            foreach (RenderSourceMaterial material in set.Materials)
                expansion = Math.Max(expansion, material.BoundsExpansion);
            set.BoundsExpansion = expansion;
        }
        foreach (Texture texture in _cookieReferences.Keys) _resources.Add(texture);
        return _resources;
    }

    internal RenderSourceMaterials? ReadMaterials(Entity source) => _sourceMaterials.GetValueOrDefault(source);

    private void RemoveMaterials(Entity source)
    {
        if (!_sourceMaterials.Remove(source, out RenderSourceMaterials? set) || --set.SourceCount != 0) return;
        _materialSets.Remove(set.Materials);
        foreach (RenderSourceMaterial material in set.Materials)
            if (--material.ReferenceCount == 0) _materialReferences.Remove(material.Material);
    }

    private void RemoveCookie(Entity source)
    {
        if (_sourceCookies.Remove(source, out Texture? texture))
            RemoveReference(_cookieReferences, texture);
    }

    private static void AddReference<T>(Dictionary<T, int> references, T resource) where T : class
    {
        references.TryGetValue(resource, out int count);
        references[resource] = count + 1;
    }

    private static void RemoveReference<T>(Dictionary<T, int> references, T resource) where T : class
    {
        int count = references[resource];
        if (count == 1) references.Remove(resource);
        else references[resource] = count - 1;
    }

    internal void Clear()
    {
        _sourceMaterials.Clear();
        _materialSets.Clear();
        _sourceCookies.Clear();
        _materialReferences.Clear();
        _cookieReferences.Clear();
        _resources.Clear();
    }

    private sealed class MaterialArrayComparison : IEqualityComparer<RenderSourceMaterial[]>
    {
        public bool Equals(RenderSourceMaterial[]? first, RenderSourceMaterial[]? second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first is null || second is null || first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++)
                if (!ReferenceEquals(first[index], second[index])) return false;
            return true;
        }

        public int GetHashCode(RenderSourceMaterial[] materials)
        {
            var hash = new HashCode();
            foreach (RenderSourceMaterial material in materials) hash.Add(material.Material.MaterialId);
            return hash.ToHashCode();
        }
    }
}

internal sealed class RenderSourceMaterial(Material material)
{
    internal Material Material { get; } = material;
    internal int ReferenceCount;
    internal float BoundsExpansion;
}

internal sealed class RenderSourceMaterials(RenderSourceMaterial[] materials)
{
    internal RenderSourceMaterial[] Materials { get; } = materials;
    internal int SourceCount;
    internal float BoundsExpansion;
}
