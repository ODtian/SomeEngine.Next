using SomeEngine.Geometry.Cluster;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace SomeEngine.Assets.Importers;

/// <summary>Builds Standard and Unlit materials while the base importer assembles glTF nodes.</summary>
[AssetImporter("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b", typeof(WorldBlock), typeof(MaterialBlock), typeof(MeshMaterialsBlock),
    SettingsProvider = typeof(GltfSceneSettingsProvider),
    VersionInputs = new[] { typeof(StandardMaterialParameters), typeof(UnlitMaterialParameters) })]
public sealed class StandardGltfSceneImporter : GltfSceneImporter
{
    public static readonly Guid ImporterId = new("9c14a7ba-e69d-4d6d-b8a1-5aec78ae6b3b");
    public static GltfSourceImporter CreateSourceImporter(IServiceCollection? importers = null,
        string? capabilitiesVersion = null)
    {
        importers ??= new ServiceCollection();
        EngineAssetCapabilities.AddAssetCapabilities(importers);
        return new(importers, capabilitiesVersion, ImporterId);
    }

    public override async ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        using MaterialAsset material = await CreateMaterialAsync(source.Unlit, context, cancellationToken).ConfigureAwait(false);
        MaterialBlock content = material.GetBlock<MaterialBlock>();
        if (source.Unlit)
        {
            var baseColor = source.FindChannel("BaseColor");
            var unlit = new UnlitMaterialParameters(content);
            unlit.BaseColorTint = baseColor?.Color ?? System.Numerics.Vector4.One;
            if (baseColor?.Texture is { } texture)
            {
                TextureAsset imported = await context.GetAssetAsync<GltfTexture, TextureAsset>(texture,
                    cancellationToken).ConfigureAwait(false);
                unlit.AlbedoMap = imported.Reference;
            }
        }
        else await ImportStandardParametersAsync(source, context, content, cancellationToken).ConfigureAwait(false);
        MaterialBlock block = content.Duplicate();
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }

    /// <summary>Applies ordinary glTF PBR values while projects retain template and shader-variant selection.</summary>
    public static async ValueTask ImportStandardParametersAsync(GltfMaterial source, ImportContext context,
        MaterialBlock content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(content);
        var baseColor = source.FindChannel("BaseColor");
        var surface = source.FindChannel("MetallicRoughness");
        var normal = source.FindChannel("Normal");
        var occlusion = source.FindChannel("Occlusion");
        var emissive = source.FindChannel("Emissive");
        var textures = new Dictionary<GltfTexture, (AssetId<TextureAsset> Id, bool SrgbView)>();
        foreach (GltfTexture? texture in new[] { baseColor?.Texture, surface?.Texture, normal?.Texture,
            occlusion?.Texture, emissive?.Texture })
        {
            if (texture is null || textures.ContainsKey(texture)) continue;
            TextureAsset imported = await context.GetAssetAsync<GltfTexture, TextureAsset>(texture,
                cancellationToken).ConfigureAwait(false);
            TextureBlock pixels = await imported.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
            textures.Add(texture, (imported.Reference, pixels.SampledFormat is
                SomeEngine.Graphics.Format.R8G8B8A8UNormSrgb or SomeEngine.Graphics.Format.B8G8R8A8UNormSrgb or
                SomeEngine.Graphics.Format.BC1UNormSrgb or SomeEngine.Graphics.Format.BC2UNormSrgb or
                SomeEngine.Graphics.Format.BC3UNormSrgb or SomeEngine.Graphics.Format.BC7UNormSrgb));
        }
        AssetId<TextureAsset> TextureOr(GltfTexture? texture, AssetId<TextureAsset> fallback)
            => texture is not null && textures.TryGetValue(texture, out var value) ? value.Id : fallback;
        bool DecodeColor(GltfTexture? texture)
            => texture is not null && textures.TryGetValue(texture, out var value) && !value.SrgbView;
        var standard = new StandardMaterialParameters(content);
        standard.BaseColorTint = baseColor?.Color ?? System.Numerics.Vector4.One;
        standard.MetallicFactor = surface?.GetFactor("MetallicFactor") ?? 1;
        standard.Roughness = surface?.GetFactor("RoughnessFactor") ?? 1;
        System.Numerics.Vector4 emission = emissive?.Color ?? System.Numerics.Vector4.Zero;
        float strength = emissive?.GetFactor("EmissiveStrength") ?? 1;
        standard.EmissiveFactor = new System.Numerics.Vector3(emission.X, emission.Y, emission.Z) * strength;
        // Missing source channels keep the caller's existing template defaults.
        if (baseColor?.Texture is { } albedoTexture) standard.AlbedoMap = textures[albedoTexture].Id;
        if (normal?.Texture is { } normalTexture) standard.NormalMap = textures[normalTexture].Id;
        // The independent AO texture is never a metallic/roughness fallback.
        if (surface?.Texture is { } surfaceTexture) standard.ARMMap = textures[surfaceTexture].Id;
        standard.OcclusionMap = TextureOr(occlusion?.Texture, MaterialTemplates.White);
        standard.EmissiveMap = TextureOr(emissive?.Texture, MaterialTemplates.White);
        standard.NormalScale = normal?.GetFactor("NormalScale") ?? 1;
        standard.OcclusionStrength = occlusion?.GetFactor("OcclusionStrength") ?? 1;
        standard.UseOcclusionMap = true;
        standard.UseEmissiveMap = true;
        // glTF color images are sRGB. A hardware sRGB view has already decoded RGB.
        standard.DecodeAlbedoSrgb = DecodeColor(baseColor?.Texture);
        standard.DecodeEmissiveSrgb = DecodeColor(emissive?.Texture);
    }

    /// <summary>Returns the texture-alpha cutoff for a caller-selected MASK variant, or -1 for another alpha mode.</summary>
    public static float GetAlphaMaskCutoff(GltfMaterial source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Alpha == SharpGLTF.Schema2.AlphaMode.MASK
            ? source.AlphaCutoff / Math.Max(source.FindChannel("BaseColor")?.Color.W ?? 1, 1e-8f) : -1;
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

