using Microsoft.Extensions.DependencyInjection;
using Game.Assets;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace Game.Importing;

/// <summary>Builds project Standard and Unlit materials while the base importer assembles glTF nodes.</summary>
[AssetImporter("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class StandardGltfSceneImporter(GltfImporterSettings sourceSettings) : GltfSceneImporter
{
    public static readonly Guid ImporterId = new("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b");
    public const string LitTemplate = "assets/Materials/Standard.material.asset";
    public const string UnlitTemplate = "assets/Materials/TestUnlit_1.material.asset";

    public static GltfSourceImporter CreateSourceImporter(Action<IServiceCollection>? configureServices = null,
        string? capabilitiesVersion = null)
        => new(services =>
        {
            ProjectAssetCapabilities.AddAssetCapabilities(services);
            services.AddSingleton<IClusterVertexEncoder>(StandardClusterVertexEncoder.Instance);
            configureServices?.Invoke(services);
        }, capabilitiesVersion ?? ProjectAssetCapabilities.BuildVersion, ImporterId);

    public override async ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        var baseColor = source.FindChannel("BaseColor");
        var metallicRoughness = source.FindChannel("MetallicRoughness");
        GltfTexture? surface = metallicRoughness?.Texture ?? source.FindChannel("Occlusion")?.Texture;
        var textures = new Dictionary<GltfTexture, AssetId<TextureAsset>>();
        GltfTexture?[] needed = source.Unlit
            ? [baseColor?.Texture]
            : [baseColor?.Texture, source.FindChannel("Normal")?.Texture, surface];
        foreach (GltfTexture? texture in needed)
        {
            if (texture is null || textures.ContainsKey(texture)) continue;
            TextureAsset imported = await context.GetAssetAsync<GltfTexture, TextureAsset>(
                texture, cancellationToken).ConfigureAwait(false);
            textures.Add(texture, imported.Reference);
        }
        string templatePath = source.Unlit ? sourceSettings.UnlitMaterialTemplate : sourceSettings.LitMaterialTemplate;
        string fullPath = Path.IsPathRooted(templatePath)
            ? Path.GetFullPath(templatePath) : Path.GetFullPath(Path.Combine(context.ProjectRoot, templatePath));
        await context.ReportInputAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await using MaterialAsset template = await MaterialAsset.OpenAsync(fullPath, cancellationToken).ConfigureAwait(false);
        using MaterialAsset material = template.CreateCopy();
        MaterialBlock content = material.GetBlock<MaterialBlock>();
        AssetId<TextureAsset> albedo = baseColor?.Texture is { } colorTexture
            && textures.TryGetValue(colorTexture, out AssetId<TextureAsset> colorId) ? colorId : default;
        switch (material)
        {
            case StandardMaterialAsset:
                var standard = new StandardMaterialParameters(content);
                var emissive = source.FindChannel("Emissive");
                standard.BaseColorTint = baseColor?.Color ?? System.Numerics.Vector4.One;
                standard.MetallicFactor = metallicRoughness?.GetFactor("MetallicFactor") ?? 1;
                standard.Roughness = metallicRoughness?.GetFactor("RoughnessFactor") ?? 1;
                System.Numerics.Vector4 emission = emissive?.Color ?? System.Numerics.Vector4.Zero;
                standard.EmissiveFactor = new(emission.X, emission.Y, emission.Z);
                if (!albedo.IsEmpty) standard.AlbedoMap = albedo;
                if (source.FindChannel("Normal")?.Texture is { } normal
                    && textures.TryGetValue(normal, out AssetId<TextureAsset> normalId)) standard.NormalMap = normalId;
                if (surface is not null && textures.TryGetValue(surface, out AssetId<TextureAsset> surfaceId))
                    standard.ARMMap = surfaceId;
                break;
            case UnlitMaterialAsset:
                var unlit = new UnlitMaterialParameters(content);
                unlit.BaseColorTint = baseColor?.Color ?? System.Numerics.Vector4.One;
                if (!albedo.IsEmpty) unlit.AlbedoMap = albedo;
                break;
            default:
                throw new InvalidDataException($"Unsupported glTF material type '{content.MaterialTypeId}'.");
        }
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}

[AssetCapabilities]
public static partial class ProjectAssetCapabilities;
