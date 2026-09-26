using Microsoft.Extensions.DependencyInjection;
using Game.Assets;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
namespace Game.Importing;
public static class MaterialImport
{
    public const string LitTemplate = "assets/Materials/Standard.material.asset";
    public const string UnlitTemplate = "assets/Materials/TestUnlit_1.material.asset";
    public static GltfSourceImporter CreateGltf(Action<IServiceCollection>? configureServices = null,
        string? capabilitiesVersion = null) => new(services =>
        {
            ProjectAssetCapabilities.AddAssetCapabilities(services);
            services.AddSingleton<IGltfMaterialConversion, ProjectGltfMaterialConversion>();
            services.AddSingleton<IClusterVertexEncoder>(StandardClusterVertexEncoder.Instance);
            configureServices?.Invoke(services);
        }, capabilitiesVersion ?? ProjectAssetCapabilities.BuildVersion);
    public static IEnumerable<SharpGLTF.Schema2.Texture> Textures(SharpGLTF.Schema2.Material material)
    {
        if (material.FindChannel("BaseColor")?.Texture is { } color) yield return color;
        if (material.Unlit) yield break;
        if (material.FindChannel("Normal")?.Texture is { } normal) yield return normal;
        if ((material.FindChannel("MetallicRoughness")?.Texture ?? material.FindChannel("Occlusion")?.Texture) is { } surface) yield return surface;
    }
    public static void Apply(MaterialAsset asset, SharpGLTF.Schema2.Material material,
        Func<SharpGLTF.Schema2.Texture?, AssetId<TextureAsset>> resolveTexture)
    {
        var baseColor = material.FindChannel("BaseColor");
        var tint = baseColor?.Color ?? System.Numerics.Vector4.One;
        AssetId<TextureAsset> albedo = resolveTexture(baseColor?.Texture);
        switch (asset)
        {
            case StandardMaterialAsset:
                var standard = new StandardMaterialParameters(asset.GetBlock<MaterialBlock>());
                var metallicRoughness = material.FindChannel("MetallicRoughness");
                var emissive = material.FindChannel("Emissive");
                standard.BaseColorTint = tint;
                standard.MetallicFactor = metallicRoughness?.GetFactor("MetallicFactor") ?? 1;
                standard.Roughness = metallicRoughness?.GetFactor("RoughnessFactor") ?? 1;
                var emission = emissive?.Color ?? System.Numerics.Vector4.Zero;
                standard.EmissiveFactor = new(emission.X, emission.Y, emission.Z);
                if (!albedo.IsEmpty) standard.AlbedoMap = albedo;
                AssetId<TextureAsset> normal = resolveTexture(material.FindChannel("Normal")?.Texture);
                if (!normal.IsEmpty) standard.NormalMap = normal;
                AssetId<TextureAsset> arm = resolveTexture(metallicRoughness?.Texture);
                if (arm.IsEmpty) arm = resolveTexture(material.FindChannel("Occlusion")?.Texture);
                if (!arm.IsEmpty) standard.ARMMap = arm;
                break;
            case UnlitMaterialAsset:
                var unlit = new UnlitMaterialParameters(asset.GetBlock<MaterialBlock>());
                unlit.BaseColorTint = tint;
                if (!albedo.IsEmpty) unlit.AlbedoMap = albedo;
                break;
            default:
                throw new InvalidDataException($"Unsupported GLTF material type '{asset.GetBlock<MaterialBlock>().MaterialTypeId}'.");
        }
    }

}

[AssetCapabilities]
public static partial class ProjectAssetCapabilities;

public sealed class ProjectGltfMaterialConversion : IGltfMaterialConversion
{
    public IEnumerable<SharpGLTF.Schema2.Texture> Textures(SharpGLTF.Schema2.Material material)
        => MaterialImport.Textures(material);

    public void Apply(MaterialAsset asset, SharpGLTF.Schema2.Material material,
        Func<SharpGLTF.Schema2.Texture?, AssetId<TextureAsset>> resolveTexture)
        => MaterialImport.Apply(asset, material, resolveTexture);
}
