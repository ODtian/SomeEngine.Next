using Texture = SomeEngine.Assets.Schema.Texture;
using Material = SomeEngine.Assets.Schema.Material;
using System.Text.Json;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using StbImageSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfTexture = SharpGLTF.Schema2.Texture;
using MeshAsset = SomeEngine.Assets.Schema.Mesh.Asset;
using TextureData = SomeEngine.Assets.Schema.TextureData;

namespace SomeEngine.Assets.Importers;

public sealed partial class GltfSourceImporter : IAssetImporter
{
    private static readonly string[] Extensions = [".gltf", ".glb"];
    public const uint ImporterVersion = 5;

    public string ImporterName => nameof(GltfSourceImporter);
    public IReadOnlyList<string> SourceExtensions => Extensions;

    public bool MatchesSourcePath(string sourcePath) =>
        SourceExtensions.Any(extension =>
            sourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
        );

    public ImportInputs? GetInputs(
        string projectRoot,
        string sourcePath,
        SourceMeta sourceMeta
    )
    {
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        return GltfDeps.ReadInputs(projectRoot, fullPath, sourceMeta, settings);
    }

    public async ValueTask<IReadOnlyList<ImportedAsset>> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        SourceMeta sourceMeta = SourceMeta.GetOrCreate(fullPath, ImporterName);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        ImportInputs fingerprint =
            GltfDeps.ReadInputs(projectRoot, fullPath, sourceMeta, settings)
            ?? throw new FileNotFoundException(
                $"One or more GLTF dependencies for '{fullPath}' could not be found."
            );
        ModelRoot model = ModelRoot.Load(fullPath);
        var context = new GltfImportContext(projectRoot, fullPath, sourceMeta, fingerprint);

        using MaterialTemplates templates = await LoadMaterialTemplatesAsync(
            context.ProjectRoot,
            settings,
            cancellationToken).ConfigureAwait(false);
        var importedTextures = new List<ImportedAsset>();
        List<ImportedMaterialInfo> importedMaterials = ImportMaterials(
            context,
            templates,
            model,
            importedTextures
        );
        cancellationToken.ThrowIfCancellationRequested();
        List<ImportedAsset> importedMeshes = ImportMeshes(
            context,
            settings,
            model,
            importedMaterials
        );

        ImportedAsset[] result = importedTextures
            .Concat(importedMaterials.Select(static info => info.ImportedAsset))
            .Concat(importedMeshes)
            .OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var outputIds = result.Select(static output => output.AssetGuid).ToHashSet();
        sourceMeta.Outputs.RemoveAll(output => !outputIds.Contains(output.AssetGuid));
        sourceMeta.ImporterVersion = fingerprint.ImporterVersion;
        sourceMeta.InputHash = fingerprint.InputHash;
        SourceMeta.Save(fullPath, sourceMeta);
        return result;
    }

    private static GltfImporterSettings LoadSettings(SourceMeta sourceMeta, string fullPath)
    {
        if (!sourceMeta.ImporterSettings.HasValue)
        {
            return GltfImporterSettings.Default();
        }

        GltfImporterSettings? settings =
            sourceMeta.ImporterSettings.Value.Deserialize<GltfImporterSettings>(
                AssetIoHelpers.JsonOptions
            );
        if (settings == null)
        {
            throw new InvalidOperationException(
                $"Source '{fullPath}' contains invalid importer settings for {nameof(GltfSourceImporter)}."
            );
        }

        if (string.IsNullOrWhiteSpace(settings.LitMaterialTemplate))
            settings.LitMaterialTemplate = GltfImporterSettings.DefaultLitMaterialTemplate;
        if (string.IsNullOrWhiteSpace(settings.UnlitMaterialTemplate))
            settings.UnlitMaterialTemplate = GltfImporterSettings.DefaultUnlitMaterialTemplate;
        return settings;
    }

    private static List<ImportedMaterialInfo> ImportMaterials(
        GltfImportContext context,
        MaterialTemplates templates,
        ModelRoot model,
        List<ImportedAsset> importedTextures
    )
    {
        var imported = new List<ImportedMaterialInfo>(model.LogicalMaterials.Count);
        for (int index = 0; index < model.LogicalMaterials.Count; index++)
        {
            imported.Add(ImportMaterial(context, templates, model.LogicalMaterials[index], index, importedTextures));
        }

        return imported;
    }

    private static async ValueTask<MaterialTemplates> LoadMaterialTemplatesAsync(
        string projectRoot,
        GltfImporterSettings settings,
        CancellationToken cancellationToken)
    {
        await using StandardMaterial.Asset lit = await StandardMaterial.Asset.OpenAsync(
            GltfDeps.FullPath(projectRoot, settings.LitMaterialTemplate), cancellationToken: cancellationToken).ConfigureAwait(false);
        await using UnlitMaterial.Asset unlit = await UnlitMaterial.Asset.OpenAsync(
            GltfDeps.FullPath(projectRoot, settings.UnlitMaterialTemplate), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new MaterialTemplates(lit.Duplicate(), unlit.Duplicate());
    }

    private static ImportedMaterialInfo ImportMaterial(
        GltfImportContext context,
        MaterialTemplates templates,
        GltfMaterial material,
        int index,
        List<ImportedAsset> importedTextures)
    {
        string materialName = string.IsNullOrWhiteSpace(material.Name)
            ? $"Material_{index}"
            : material.Name;
        ExportPath exportPath = MaterialExportPath(context, index, materialName);
        using Material.Asset asset = CreateMaterialAsset(context, templates, material, materialName, exportPath);

        ApplyMaterialSemantics(asset, material, context, importedTextures);
        AssetEntry entry = SaveMaterialAsset(context, asset, exportPath);
        return new ImportedMaterialInfo(index, new MeshMaterialSlot(asset.Id.Value),
            ImportedAsset.Create(entry, exportPath.SubAssetKey, exportPath.OutputPath));
    }

    private static Material.Asset CreateMaterialAsset(
        GltfImportContext context,
        MaterialTemplates templates,
        GltfMaterial material,
        string materialName,
        ExportPath exportPath)
    {
        Material.Asset template = material.Unlit ? templates.Unlit : templates.Lit;
        Guid assetGuid = ImportedAssetIds.Create(context.SourceMeta.SourceGuid, exportPath.SubAssetKey);
        var asset = template.Duplicate(new(assetGuid));
        asset.Name = materialName;
        return asset;
    }

    private static AssetEntry SaveMaterialAsset(
        GltfImportContext context,
        Material.Asset asset,
        ExportPath exportPath)
    {
        AssetEntry entry = asset.SaveAsync(exportPath.OutputPath).AsTask().GetAwaiter().GetResult();
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            asset.Id.Value.ToString(),
            context.SourceMeta,
            context.FullSourcePath,
            exportPath.SubAssetKey,
            context.Fingerprint);
        return entry;
    }

    private static ExportPath MaterialExportPath(
        GltfImportContext context,
        int index,
        string materialName)
    {
        string safeName = SanitizeSegment(materialName, $"Material_{index}");
        return new ExportPath(
            Path.Combine(
                context.SourceDirectory,
                $"{context.SourceStem}.material.{index}.{safeName}.material.asset"),
            $"material:{index}:{safeName}",
            safeName);
    }

    private static List<ImportedAsset> ImportMeshes(
        GltfImportContext context,
        GltfImporterSettings settings,
        ModelRoot model,
        IReadOnlyList<ImportedMaterialInfo> importedMaterials
    )
    {
        var imported = new List<ImportedAsset>(model.LogicalMeshes.Count);
        foreach (GltfMesh mesh in model.LogicalMeshes)
        {
            imported.Add(ImportMesh(context, settings, mesh, importedMaterials));
        }

        return imported;
    }

    private static ImportedAsset ImportMesh(
        GltfImportContext context,
        GltfImporterSettings settings,
        GltfMesh mesh,
        IReadOnlyList<ImportedMaterialInfo> importedMaterials)
    {
        int meshIndex = mesh.LogicalIndex;
        string meshName = string.IsNullOrWhiteSpace(mesh.Name)
            ? $"Mesh_{meshIndex}"
            : mesh.Name;
        ExportPath exportPath = MeshExportPath(context, meshIndex, meshName);
        var request = new MeshImportRequest(context, settings, mesh, importedMaterials, meshName, exportPath);
        using MeshAsset meshAsset = CreateMeshAsset(request);
        AssetEntry entry = SaveMeshAsset(context, meshAsset, exportPath);
        return ImportedAsset.Create(entry, exportPath.SubAssetKey, exportPath.OutputPath);
    }

    private static MeshAsset CreateMeshAsset(MeshImportRequest request)
    {
        using MeshAsset meshAsset = BuildClusterMeshAsset(request);
        Guid id = ImportedAssetIds.Create(request.Context.SourceMeta.SourceGuid, request.ExportPath.SubAssetKey);
        return meshAsset.Duplicate(new(id));
    }

    private static MeshAsset BuildClusterMeshAsset(MeshImportRequest request)
    {
        return ClusterBuilder.ProcessMesh(
            request.Mesh,
            MeshMaterialSlots(request.Mesh, request.ImportedMaterials),
            request.MeshName,
            MeshBuilderOptions(request.Settings));
    }

    private static ClusterBuilderOptions MeshBuilderOptions(GltfImporterSettings settings)
    {
        return new ClusterBuilderOptions
        {
            GenerateMissingTangents = settings.GenerateTangents,
        };
    }


}

public sealed partial class GltfSourceImporter
{


    private static AssetEntry SaveMeshAsset(
        GltfImportContext context,
        MeshAsset meshAsset,
        ExportPath exportPath)
    {
        AssetEntry entry = meshAsset.SaveAsync(exportPath.OutputPath).AsTask().GetAwaiter().GetResult();
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            meshAsset.Id.Value.ToString(),
            context.SourceMeta,
            context.FullSourcePath,
            exportPath.SubAssetKey,
            context.Fingerprint);
        return entry;
    }

    private static IReadOnlyList<MeshMaterialSlot> MeshMaterialSlots(
        GltfMesh mesh,
        IReadOnlyList<ImportedMaterialInfo> importedMaterials)
    {
        return mesh
            .Primitives.Select(static primitive => primitive.Material?.LogicalIndex ?? -1)
            .Select(index =>
                index >= 0 && index < importedMaterials.Count
                    ? importedMaterials[index].MeshMaterialSlot
                    : throw new InvalidDataException(
                        "Every imported mesh primitive must reference a material with a deform implementation."))
            .ToArray();
    }

    private static ExportPath MeshExportPath(
        GltfImportContext context,
        int meshIndex,
        string meshName)
    {
        string safeName = SanitizeSegment(meshName, $"Mesh_{meshIndex}");
        return new ExportPath(
            Path.Combine(
                context.SourceDirectory,
                $"{context.SourceStem}.mesh.{meshIndex}.{safeName}.mesh.asset"),
            $"mesh:{meshIndex}:{safeName}",
            safeName);
    }

    private static void ApplyMaterialSemantics(Material.Asset asset, GltfMaterial material,
        GltfImportContext context, List<ImportedAsset> importedTextures)
    {
        var baseColor = material.FindChannel("BaseColor");
        var tint = new System.Numerics.Vector4(baseColor?.Parameter.X ?? 1, baseColor?.Parameter.Y ?? 1,
            baseColor?.Parameter.Z ?? 1, baseColor?.Parameter.W ?? 1);
        AssetId<Texture> albedo = ResolveTextureAsset(baseColor?.Texture, context, importedTextures);
        switch (asset)
        {
            case StandardMaterial.Asset standard:
                var metallicRoughness = material.FindChannel("MetallicRoughness");
                var emissive = material.FindChannel("Emissive");
                standard.BaseColorTint = tint;
                standard.MetallicFactor = metallicRoughness?.Parameter.X ?? 1;
                standard.Roughness = metallicRoughness?.Parameter.Y ?? 1;
                standard.EmissiveFactor = new(emissive?.Parameter.X ?? 0, emissive?.Parameter.Y ?? 0, emissive?.Parameter.Z ?? 0);
                if (!albedo.IsEmpty) standard.AlbedoMap = albedo;
                AssetId<Texture> normal = ResolveTextureAsset(material.FindChannel("Normal")?.Texture, context, importedTextures);
                if (!normal.IsEmpty) standard.NormalMap = normal;
                AssetId<Texture> arm = ResolveTextureAsset(metallicRoughness?.Texture, context, importedTextures);
                if (arm.IsEmpty) arm = ResolveTextureAsset(material.FindChannel("Occlusion")?.Texture, context, importedTextures);
                if (!arm.IsEmpty) standard.ARMMap = arm;
                break;
            case UnlitMaterial.Asset unlit:
                unlit.BaseColorTint = tint;
                if (!albedo.IsEmpty) unlit.AlbedoMap = albedo;
                break;
            default:
                throw new InvalidDataException($"Unsupported GLTF material type '{asset.MaterialTypeId}'.");
        }
    }

    /// <summary>
    /// Extract texture from GLTF, produce a .texture.asset file, return the Guid string.
    /// </summary>
    private static AssetId<Texture> ResolveTextureAsset(
        GltfTexture? texture,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        if (texture?.PrimaryImage == null)
        {
            return default;
        }

        Image image = texture.PrimaryImage;
        ExportPath exportPath = TextureExportPath(context, texture, image);
        using Texture.Asset textureAsset = CreateTextureAsset(context, exportPath, image);
        AssetEntry entry = SaveTextureAsset(context, textureAsset, exportPath);
        importedTextures.Add(ImportedAsset.Create(entry, exportPath.SubAssetKey, exportPath.OutputPath));
        return textureAsset.Id;
    }

    private static ExportPath TextureExportPath(
        GltfImportContext context,
        GltfTexture texture,
        Image image)
    {
        string imageName = SanitizeSegment(image.Name, $"image_{texture.LogicalIndex}");
        return new ExportPath(
            Path.Combine(
                context.SourceDirectory,
                $"{context.SourceStem}.texture.{texture.LogicalIndex}.{imageName}.texture.asset"),
            $"texture:{texture.LogicalIndex}:{imageName}",
            imageName);
    }

    private static Texture.Asset CreateTextureAsset(
        GltfImportContext context,
        ExportPath exportPath,
        Image image)
    {
        Guid assetGuid = ImportedAssetIds.Create(context.SourceMeta.SourceGuid, exportPath.SubAssetKey);
        ImageResult decoded = ImageResult.FromMemory(
            ReadImageBytes(image).ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        byte[] pixels = decoded.Data;
        uint width = checked((uint)decoded.Width);
        uint height = checked((uint)decoded.Height);
        return new Texture.Asset(new AssetId<Texture>(assetGuid))
        {
            Name = exportPath.SafeName,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Width = width,
            Height = height,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles =
            [
                new TextureMipTile
                {
                    MipLevel = 0,
                    TileX = 0,
                    TileY = 0,
                    Width = width,
                    Height = height,
                    RowPitch = checked(width * 4u),
                    SlicePitch = checked((ulong)width * height * 4u),
                    Payload = pixels,
                },
            ],
        };
    }

    private static AssetEntry SaveTextureAsset(
        GltfImportContext context,
        Texture.Asset textureAsset,
        ExportPath exportPath)
    {
        AssetEntry entry = textureAsset.SaveAsync(exportPath.OutputPath).AsTask().GetAwaiter().GetResult();
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            textureAsset.Id.Value.ToString(),
            context.SourceMeta,
            context.FullSourcePath,
            exportPath.SubAssetKey,
            context.Fingerprint);
        return entry;
    }

    private static Memory<byte> ReadImageBytes(Image image)
    {
        var content = image.Content;
        if (!string.IsNullOrWhiteSpace(content.SourcePath))
            return File.ReadAllBytes(content.SourcePath!);

        if (!MemoryMarshal.TryGetArray(content.Content, out ArraySegment<byte> bytes)
            || bytes.Array is null)
        {
            throw new InvalidDataException(
                "Embedded glTF images must expose their single array-backed payload; " +
                "copying a non-array image backing is not permitted.");
        }

        return bytes.Array.AsMemory(bytes.Offset, bytes.Count);
    }

    private static string SanitizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        string sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private readonly record struct ImportedMaterialInfo(
        int MaterialIndex,
        MeshMaterialSlot MeshMaterialSlot,
        ImportedAsset ImportedAsset
    );

    private readonly record struct GltfImportContext(
        string ProjectRoot,
        string FullSourcePath,
        SourceMeta SourceMeta,
        ImportInputs Fingerprint)
    {
        public string SourceStem => Path.GetFileNameWithoutExtension(FullSourcePath);
        public string SourceDirectory => Path.GetDirectoryName(FullSourcePath)!;
    }

    private sealed record MaterialTemplates(StandardMaterial.Asset Lit, UnlitMaterial.Asset Unlit) : IDisposable
    {
        public void Dispose() { Lit.Dispose(); Unlit.Dispose(); }
    }

    private readonly record struct ExportPath(
        string OutputPath,
        string SubAssetKey,
        string SafeName);

    private readonly record struct MeshImportRequest(
        GltfImportContext Context,
        GltfImporterSettings Settings,
        GltfMesh Mesh,
        IReadOnlyList<ImportedMaterialInfo> ImportedMaterials,
        string MeshName,
        ExportPath ExportPath);
}

