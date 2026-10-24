using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

/// <summary>Editable engine material defaults saved as ordinary project assets.</summary>
public static class MaterialTemplates
{
    public static readonly AssetId<TextureAsset> White = new(new Guid("da8198af-5d1b-4804-ab6f-db822185c813"));
    public static readonly AssetId<TextureAsset> FlatNormal = new(new Guid("0a9c7ec2-df71-438f-bd44-17fdea284fbe"));
    public static readonly AssetId<MaterialAsset> Standard = new(new Guid("d0848ef2-57ee-47b4-a52d-6a979ea6b060"));
    public static readonly AssetId<MaterialAsset> Unlit = new(new Guid("20697502-2e7d-4927-9a40-93d2b1812463"));
    public const string StandardPath = "assets/Materials/Standard.material.asset";
    public const string UnlitPath = "assets/Materials/TestUnlit_1.material.asset";

    public static MaterialAsset CreateStandard(AssetId<ShaderAsset> shader)
    {
        var asset = new MaterialAsset(Standard.Value) { Name = "Standard" };
        MaterialBlock content = asset.GetBlock<MaterialBlock>();
        content.Shader = shader;
        _ = new StandardMaterialParameters(content) { AlbedoMap = White, NormalMap = FlatNormal, ARMMap = White };
        return asset;
    }

    public static MaterialAsset CreateUnlit(AssetId<ShaderAsset> shader)
    {
        var asset = new MaterialAsset(Unlit.Value) { Name = "Unlit" };
        MaterialBlock content = asset.GetBlock<MaterialBlock>();
        content.Shader = shader;
        new UnlitMaterialParameters(content).AlbedoMap = White;
        return asset;
    }

    public static async ValueTask SaveAsync(AssetProject project, AssetId<ShaderAsset> standardShader,
        AssetId<ShaderAsset> unlitShader, CancellationToken cancellationToken = default)
    {
        using TextureAsset white = CreateTexture(White, "default_white", [255, 255, 255, 255]);
        project.RegisterAsset(await white.SaveAsync(Path.Combine(project.RootPath, "assets/Textures/default_white.texture.asset"),
            cancellationToken).ConfigureAwait(false));
        using TextureAsset normal = CreateTexture(FlatNormal, "default_normal", [128, 128, 255, 255]);
        project.RegisterAsset(await normal.SaveAsync(Path.Combine(project.RootPath, "assets/Textures/default_normal.texture.asset"),
            cancellationToken).ConfigureAwait(false));
        using MaterialAsset standard = CreateStandard(standardShader);
        project.RegisterAsset(await standard.SaveAsync(Path.Combine(project.RootPath, StandardPath), cancellationToken).ConfigureAwait(false));
        using MaterialAsset unlit = CreateUnlit(unlitShader);
        project.RegisterAsset(await unlit.SaveAsync(Path.Combine(project.RootPath, UnlitPath), cancellationToken).ConfigureAwait(false));
    }

    private static TextureAsset CreateTexture(AssetId<TextureAsset> id, string name, byte[] rgba)
    {
        using var pixels = new TextureBlock
        {
            Dimension = TextureDimension.Texture2D, Width = 1, Height = 1, Depth = 1,
            MipLevelCount = 1, ArrayLayerCount = 1, Format = Format.R8G8B8A8UNorm,
            SampledFormat = Format.R8G8B8A8UNorm, SampledDimension = TextureViewDimension.Texture2D,
        };
        ulong key = TextureTiles.ChunkKey(0, 0, 0);
        pixels.MipTiles.Add(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4,
            ChunkKey = key, DecodedLength = 4 });
        pixels.SetBytes(key, rgba);
        var asset = new TextureAsset(id.Value) { Name = name };
        asset.SetBlock(pixels);
        return asset;
    }
}
