using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Resources;

internal static class MaterialTextures
{
    internal static Texture Create(string name, byte[] color)
    {
        var description = new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled, label: name);
        var tile = new TextureMipTile
        {
            Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4,
            ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 4,
        };
        return Texture.Create(description, [new TexturePixels(tile, color)],
            SomeEngine.Graphics.TextureViewDimension.Texture2D);
    }
}
