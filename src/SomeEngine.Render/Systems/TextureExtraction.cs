using System.Collections.Immutable;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Render.Systems;

internal static class TextureExtraction
{
    internal static ImmutableDictionary<Texture, TextureVersions> Collect(QueryCursor query)
    {
        var textures = ImmutableDictionary.CreateBuilder<Texture, TextureVersions>();
        var materials = new HashSet<Material>();
        foreach (QueryChunkView chunk in query.Chunks)
        {
            if (chunk.HasBuffer<MeshMaterialBinding>())
                for (int row = 0; row < chunk.Entities.Length; row++)
                    foreach (MeshMaterialBinding binding in chunk.ReadBuffer<MeshMaterialBinding>(row).AsSpan())
                        materials.Add(binding.Material);
            if (chunk.TryRead<LightCookie>(out ReadOnlySpan<LightCookie> cookies))
                foreach (LightCookie cookie in cookies)
                    if (cookie.Texture is { } texture) textures[texture] = texture.Versions;
        }
        foreach (Material material in materials)
            for (uint property = 0; property < material.PropertyCount; property++)
                if (material.GetTexture(property) is { } texture) textures[texture] = texture.Versions;
        return textures.ToImmutable();
    }
}
