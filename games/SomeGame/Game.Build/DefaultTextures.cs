using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;

namespace Game.Build;

/// <summary>Project-owned default image sources. Pixel data is built directly into existing texture pages.</summary>
internal static class DefaultTextures
{
    internal static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        await BuildAsync(project, DefaultContentIds.WhiteTexture, "default_white", [255, 255, 255, 255], cancellationToken).ConfigureAwait(false);
        await BuildAsync(project, DefaultContentIds.NormalTexture, "default_normal", [128, 128, 255, 255], cancellationToken).ConfigureAwait(false);
        await BuildAsync(project, DefaultContentIds.ArmTexture, "default_arm", [255, 255, 255, 255], cancellationToken).ConfigureAwait(false);
    }

    private static async Task BuildAsync(AssetProject project, Guid id, string name, byte[] rgba, CancellationToken cancellationToken)
    {
        using var pixels = new TextureBlock
        {
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        ulong key = TextureTiles.ChunkKey(0, 0, 0);
        pixels.MipTiles.Add(new TextureMipTile
        {
            Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4, ChunkKey = key, DecodedLength = 4,
        });
        pixels.SetBytes(key, rgba);
        using var asset = new TextureAsset(id) { Name = name };
        asset.SetBlock(pixels);
        AssetEntry entry = await asset.SaveAsync(Path.Combine(project.RootPath, $"assets/Textures/{name}.texture.asset"), cancellationToken).ConfigureAwait(false);
        project.RegisterAsset(entry);
    }
}
