using SharpGLTF.Schema2;
using StbImageSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

[AssetImporter("451b3ab1-e318-4ed8-aebf-9b9e11219120", typeof(TextureBlock), SettingsProvider = typeof(GltfTextureSettingsProvider))]
public sealed class GltfTextureImporter : IAssetImporter<GltfTexture, GltfTextureSettings>
{
    public ValueTask ImportAsync(GltfTexture source, GltfTextureSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Image image = source.PrimaryImage ?? throw new InvalidDataException("The glTF texture has no image.");
        byte[] sourceBytes = !string.IsNullOrWhiteSpace(image.Content.SourcePath)
            ? File.ReadAllBytes(image.Content.SourcePath) : image.Content.Content.ToArray();
        ImageResult pixels = ImageResult.FromMemory(sourceBytes, ColorComponents.RedGreenBlueAlpha);
        uint width = checked((uint)pixels.Width), height = checked((uint)pixels.Height);
        var texture = new TextureBlock(output.Target.Pages)
        {
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D, Width = width, Height = height, Depth = 1,
            MipLevelCount = 1, ArrayLayerCount = 1, Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        try
        {
            ulong key = TextureTiles.ChunkKey(0, 0, 0);
            texture.MipTiles.Add(new TextureMipTile { MipLevel = 0, TileX = 0, TileY = 0, Width = width, Height = height,
                RowPitch = checked(width * 4), SlicePitch = checked((ulong)width * height * 4), ChunkKey = key, DecodedLength = checked((ulong)pixels.Data.Length) });
            texture.Chunks.Set(key, pixels.Data);
            cancellationToken.ThrowIfCancellationRequested();
            output.SetBlock(texture);
        }
        catch { texture.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
