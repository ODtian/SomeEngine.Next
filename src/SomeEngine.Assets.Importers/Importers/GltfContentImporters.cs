using SharpGLTF.Schema2;
using StbImageSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

[AssetImporter("ab43bf52-332c-4439-b3a7-e214c7cf63ad", typeof(MeshMaterialsBlock), SettingsProvider = typeof(GltfMeshMaterialSettingsProvider))]
public sealed class GltfMeshMaterialsImporter : IAssetImporter<GltfMesh, GltfMeshMaterialSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, GltfMeshMaterialSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        var references = new MeshMaterialsBlock(output.Target.Pages);
        try
        {
            foreach (var primitive in source.Primitives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetId<MaterialAsset> material = default;
                if (primitive.Material is not null)
                {
                    MaterialAsset imported = await context.GetAssetAsync<GltfMaterial, MaterialAsset>(
                        primitive.Material, cancellationToken).ConfigureAwait(false);
                    material = imported.Reference;
                }
                references.Materials.Add(material);
            }
            output.SetBlock(references);
        }
        catch { references.Dispose(); throw; }
    }
}

[AssetImporter("460b170b-0d65-4d55-89ed-4a2165935a27", typeof(MaterialBlock), SettingsProvider = typeof(GltfMaterialSettingsProvider))]
public sealed class GltfMaterialImporter(GltfImporterSettings sourceSettings, IGltfMaterialConversion conversion) : IAssetImporter<GltfMaterial, GltfMaterialSettings>
{
    public async ValueTask ImportAsync(GltfMaterial source, GltfMaterialSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        var textures = new Dictionary<GltfTexture, AssetId<TextureAsset>>();
        if (settings.ImportTextures)
        {
            foreach (GltfTexture texture in conversion.Textures(source))
                if (!textures.ContainsKey(texture))
                {
                    TextureAsset imported = await context.GetAssetAsync<GltfTexture, TextureAsset>(texture, cancellationToken).ConfigureAwait(false);
                    textures.Add(texture, imported.Reference);
                }
        }
        string templatePath = source.Unlit ? sourceSettings.UnlitMaterialTemplate : sourceSettings.LitMaterialTemplate;
        await using MaterialAsset template = await MaterialAsset.OpenAsync(
            GltfDeps.FullPath(context.ProjectRoot, templatePath), cancellationToken).ConfigureAwait(false);
        using MaterialAsset material = template.CreateCopy();
        conversion.Apply(material, source, texture => texture is not null && textures.TryGetValue(texture, out var id) ? id : default);
        MaterialBlock block = material.GetBlock<MaterialBlock>().Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}

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
