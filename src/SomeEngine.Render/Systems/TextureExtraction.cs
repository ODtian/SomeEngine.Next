using System.Collections.Immutable;
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

internal static class TextureExtraction
{
    internal static RenderExtractionResources Collect(scoped QueryCursor query)
    {
        var resources = new RenderExtractionResources();
        foreach (QueryChunkView chunk in query.Chunks)
        {
            if (chunk.TryRead<LightCookie>(out ReadOnlySpan<LightCookie> cookies))
                foreach (LightCookie cookie in cookies)
                    if (cookie.Texture is { } texture) resources.Add(texture);
        }
        return resources;
    }
}
