using SomeEngine.Geometry.Cluster;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

/// <summary>Builds Standard and Unlit materials while the base importer assembles glTF nodes.</summary>
[AssetImporter("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class StandardGltfSceneImporter : GltfSceneImporter
{
    public static readonly Guid ImporterId = new("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b");
    public static GltfSourceImporter CreateSourceImporter(IServiceCollection? importers = null,
        string? capabilitiesVersion = null)
    {
        importers ??= new ServiceCollection();
        EngineAssetCapabilities.AddAssetCapabilities(importers);
        return new(importers, capabilitiesVersion ?? EngineAssetCapabilities.BuildVersion, ImporterId);
    }

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
        using MaterialAsset material = await CreateMaterialAsync(source.Unlit, context, cancellationToken).ConfigureAwait(false);
        MaterialBlock content = material.GetBlock<MaterialBlock>();
        AssetId<TextureAsset> albedo = baseColor?.Texture is { } colorTexture
            && textures.TryGetValue(colorTexture, out AssetId<TextureAsset> colorId) ? colorId : default;
        if (source.Unlit)
        {
            var unlit = new UnlitMaterialParameters(content);
            unlit.BaseColorTint = baseColor?.Color ?? System.Numerics.Vector4.One;
            if (!albedo.IsEmpty) unlit.AlbedoMap = albedo;
        }
        else
        {
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
        }
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    private async ValueTask<MaterialAsset> CreateMaterialAsync(bool unlit, ImportContext context,
        CancellationToken cancellationToken)
    {
        GltfImporterSettings sourceSettings = context.GetSettings(GltfSourceImporter.SourceImporterId,
            new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);
        string templatePath = unlit ? sourceSettings.UnlitMaterialTemplate ?? MaterialTemplates.UnlitPath
            : sourceSettings.LitMaterialTemplate ?? MaterialTemplates.StandardPath;
        string path = GltfDeps.FullPath(context.ProjectRoot, templatePath);
        await context.ReportInputAsync(path, cancellationToken).ConfigureAwait(false);
        using MaterialAsset template = await MaterialAsset.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var material = new MaterialAsset();
        material.GetBlock<MaterialBlock>().Parent = template.Reference;
        return material;
    }
}

[AssetCapabilities(GltfImporterSets.ClusterId)]
public static partial class EngineAssetCapabilities;

