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
    public void Add(Texture texture) => _textures[texture] = texture.Versions;
    public void Add(Material material)
    {
        if (_materials.ContainsKey(material.MaterialId)) return;
        _materials.Add(material.MaterialId, new(material));
        for (uint property = 0; property < material.PropertyCount; property++)
            if (material.GetTexture(property) is { } texture) Add(texture);
    }
    internal ImmutableDictionary<Texture, TextureVersions> Textures => _textures.ToImmutable();
    internal ImmutableDictionary<long, RenderMaterialReference> Materials => _materials.ToImmutable();
}

internal sealed class RenderSourceResources
{
    private readonly Dictionary<Entity, Material[]> _sourceMaterials = [];
    private readonly Dictionary<Entity, Texture> _sourceCookies = [];
    private readonly Dictionary<Material, int> _materialReferences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Texture, int> _cookieReferences = new(ReferenceEqualityComparer.Instance);

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
                    var materials = new Material[bindings.Length];
                    for (int index = 0; index < bindings.Length; index++)
                    {
                        Material material = materials[index] = bindings[index].Material;
                        AddReference(_materialReferences, material);
                    }
                    _sourceMaterials.Add(source, materials);
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
        var resources = new RenderExtractionResources();
        foreach (Material material in _materialReferences.Keys) resources.Add(material);
        foreach (Texture texture in _cookieReferences.Keys) resources.Add(texture);
        return resources;
    }

    internal float ReadMaterialBoundsExpansion(Entity source)
    {
        float expansion = 0;
        if (_sourceMaterials.TryGetValue(source, out Material[]? materials))
            foreach (Material material in materials) expansion = Math.Max(expansion, material.BoundsExpansion);
        return expansion;
    }

    private void RemoveMaterials(Entity source)
    {
        if (_sourceMaterials.Remove(source, out Material[]? materials))
            foreach (Material material in materials) RemoveReference(_materialReferences, material);
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
        _sourceCookies.Clear();
        _materialReferences.Clear();
        _cookieReferences.Clear();
    }
}
