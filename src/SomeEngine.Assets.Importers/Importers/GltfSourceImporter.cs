using System.Text.Json;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using StbImageSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfTexture = SharpGLTF.Schema2.Texture;
using Material = SomeEngine.Assets.Schema.Material;
using Mesh = SomeEngine.Assets.Schema.Mesh;
using Texture = SomeEngine.Assets.Schema.Texture;

namespace SomeEngine.Assets.Importers;

public sealed partial class GltfSourceImporter : IAssetImporter
{
    private static readonly string[] Extensions = [".gltf", ".glb"];
    public const uint ImporterVersion = 4;

    public string ImporterName => nameof(GltfSourceImporter);
    public IReadOnlyList<string> SourceExtensions => Extensions;

    public bool MatchesSourcePath(string sourcePath) =>
        SourceExtensions.Any(extension =>
            sourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
        );

    public AssetImportFingerprint? GetFingerprint(
        string projectRoot,
        string sourcePath,
        SourceMeta sourceMeta
    )
    {
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        return GltfDeps.Fingerprint(projectRoot, fullPath, sourceMeta, settings);
    }

    public async ValueTask<IReadOnlyList<ImportedAsset>> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = GltfDeps.FullPath(projectRoot, sourcePath);
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(fullPath, ImporterName);
        GltfImporterSettings settings = LoadSettings(sourceMeta, fullPath);
        AssetImportFingerprint fingerprint =
            GltfDeps.Fingerprint(projectRoot, fullPath, sourceMeta, settings)
            ?? throw new FileNotFoundException(
                $"One or more GLTF dependencies for '{fullPath}' could not be found."
            );
        ModelRoot model = ModelRoot.Load(fullPath);
        var context = new GltfImportContext(projectRoot, fullPath, sourceMeta, fingerprint);

        MaterialTemplates templates = await LoadMaterialTemplatesAsync(
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
        List<ImportedAsset> importedMeshes = ImportMeshes(
            context,
            settings,
            model,
            importedMaterials
        );

        return importedTextures
            .Concat(importedMaterials.Select(static info => info.ImportedAsset))
            .Concat(importedMeshes)
            .OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal)
            .ToArray();
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
        Task<Material> lit = AssetProject.ReadAsync<Material>(
            GltfDeps.FullPath(projectRoot, settings.LitMaterialTemplate),
            cancellationToken).AsTask();
        Task<Material> unlit = AssetProject.ReadAsync<Material>(
            GltfDeps.FullPath(projectRoot, settings.UnlitMaterialTemplate),
            cancellationToken).AsTask();
        await Task.WhenAll(lit, unlit).ConfigureAwait(false);
        return new MaterialTemplates(
            await lit.ConfigureAwait(false),
            await unlit.ConfigureAwait(false));
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
        Material asset = CreateMaterialAsset(context, templates, material, materialName, exportPath);

        ApplyMaterialSemantics(asset, material, context, importedTextures);
        SaveMaterialAsset(context, asset, exportPath);
        return CreateImportedMaterialInfo(index, asset, exportPath);
    }

    private static Material CreateMaterialAsset(
        GltfImportContext context,
        MaterialTemplates templates,
        GltfMaterial material,
        string materialName,
        ExportPath exportPath)
    {
        Material template = material.Unlit ? templates.Unlit : templates.Lit;
        AssetGuid assetGuid = AssetGuid.FromSource(context.SourceMeta.SourceGuid, exportPath.SubAssetKey);
        return CloneMaterial(template, materialName, assetGuid);
    }

    private static void SaveMaterialAsset(
        GltfImportContext context,
        Material asset,
        ExportPath exportPath)
    {
        AssetWriter.Write(asset, exportPath.OutputPath);
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            asset.AssetGuid,
            context.SourceMeta.SourceGuid,
            exportPath.SubAssetKey,
            context.Fingerprint);
    }

    private static ImportedMaterialInfo CreateImportedMaterialInfo(
        int index,
        Material asset,
        ExportPath exportPath)
    {
        return new ImportedMaterialInfo(
            index,
            new MeshMaterialSlot(AssetGuid.Parse(asset.AssetGuid!)),
            ImportedAsset.Create(asset, exportPath.SubAssetKey, exportPath.OutputPath));
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
        Mesh meshAsset = CreateMeshAsset(request);
        SaveMeshAsset(context, meshAsset, exportPath);
        return CreateImportedMeshAsset(meshAsset, exportPath);
    }

    private static Mesh CreateMeshAsset(MeshImportRequest request)
    {
        Mesh meshAsset = BuildClusterMeshAsset(request);
        AssignMeshGuid(request, meshAsset);
        return meshAsset;
    }

    private static Mesh BuildClusterMeshAsset(MeshImportRequest request)
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

    private static void AssignMeshGuid(MeshImportRequest request, Mesh meshAsset)
    {
        AssetGuid assetGuid = AssetGuid.FromSource(
            request.Context.SourceMeta.SourceGuid,
            request.ExportPath.SubAssetKey);
        meshAsset.AssetGuid = assetGuid.ToFlatString();
    }
}

public sealed partial class GltfSourceImporter
{


    private static void SaveMeshAsset(
        GltfImportContext context,
        Mesh meshAsset,
        ExportPath exportPath)
    {
        AssetWriter.Write(meshAsset, exportPath.OutputPath);
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            meshAsset.AssetGuid,
            context.SourceMeta.SourceGuid,
            exportPath.SubAssetKey,
            context.Fingerprint);
    }

    private static ImportedAsset CreateImportedMeshAsset(
        Mesh meshAsset,
        ExportPath exportPath)
    {
        return ImportedAsset.Create(meshAsset, exportPath.SubAssetKey, exportPath.OutputPath);
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

    private static Material CloneMaterial(
        Material template,
        string materialName,
        AssetGuid assetGuid
    ) => template.CloneForAuthoring(
        materialName,
        assetGuid.IsEmpty ? AssetGuid.New() : assetGuid);

    private static void ApplyMaterialSemantics(
        Material asset,
        GltfMaterial material,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        switch (asset)
        {
            case StandardMaterial standard:
                ApplyStandardMaterial(standard, material, context, importedTextures);
                break;
            case UnlitMaterial unlit:
                ApplyUnlitMaterial(unlit, material, context, importedTextures);
                break;
            default:
                throw new InvalidDataException(
                    $"GLTF material templates must be concrete supported material types; got '{asset.GetType().FullName}'.");
        }
    }

    private static void ApplyStandardMaterial(
        StandardMaterial asset,
        GltfMaterial material,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        var baseColor = material.FindChannel("BaseColor");
        var metallicRoughness = material.FindChannel("MetallicRoughness");
        var emissive = material.FindChannel("Emissive");
        asset.BaseColorTint = new System.Numerics.Vector4(
            baseColor?.Parameter.X ?? 1.0f,
            baseColor?.Parameter.Y ?? 1.0f,
            baseColor?.Parameter.Z ?? 1.0f,
            baseColor?.Parameter.W ?? 1.0f);
        asset.MetallicFactor = metallicRoughness?.Parameter.X ?? 1.0f;
        asset.Roughness = metallicRoughness?.Parameter.Y ?? 1.0f;
        asset.EmissiveFactor = new System.Numerics.Vector3(
            emissive?.Parameter.X ?? 0.0f,
            emissive?.Parameter.Y ?? 0.0f,
            emissive?.Parameter.Z ?? 0.0f);
        ApplyTextureBinding(asset, "AlbedoMap", material.FindChannel("BaseColor")?.Texture, context, importedTextures);
        ApplyTextureBinding(asset, "NormalMap", material.FindChannel("Normal")?.Texture, context, importedTextures);
        ApplyArmTextureBinding(asset, material, context, importedTextures);
    }

    private static void ApplyUnlitMaterial(
        UnlitMaterial asset,
        GltfMaterial material,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        var baseColor = material.FindChannel("BaseColor");
        asset.BaseColorTint = new System.Numerics.Vector4(
            baseColor?.Parameter.X ?? 1.0f,
            baseColor?.Parameter.Y ?? 1.0f,
            baseColor?.Parameter.Z ?? 1.0f,
            baseColor?.Parameter.W ?? 1.0f);
        ApplyTextureBinding(asset, "AlbedoMap", baseColor?.Texture, context, importedTextures);
    }

    private static void ApplyArmTextureBinding(
        Material asset,
        GltfMaterial material,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        string? armGuid = ResolveTextureAsset(
            material.FindChannel("MetallicRoughness")?.Texture,
            context,
            importedTextures)
            ?? ResolveTextureAsset(
                material.FindChannel("Occlusion")?.Texture,
                context,
                importedTextures);
        if (!string.IsNullOrWhiteSpace(armGuid))
        {
            SetTexture(asset, "ARMMap", armGuid);
        }
    }

    private static void ApplyTextureBinding(
        Material asset,
        string name,
        GltfTexture? texture,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        string? textureGuid = ResolveTextureAsset(texture, context, importedTextures);
        if (!string.IsNullOrWhiteSpace(textureGuid))
        {
            SetTexture(asset, name, textureGuid);
        }
    }

    /// <summary>
    /// Extract texture from GLTF, produce a .texture.asset file, return the AssetGuid string.
    /// </summary>
    private static string? ResolveTextureAsset(
        GltfTexture? texture,
        GltfImportContext context,
        List<ImportedAsset> importedTextures)
    {
        if (texture?.PrimaryImage == null)
        {
            return null;
        }

        Image image = texture.PrimaryImage;
        ExportPath exportPath = TextureExportPath(context, texture, image);
        Texture textureAsset = CreateTextureAsset(context, exportPath, image);
        SaveTextureAsset(context, textureAsset, exportPath);
        importedTextures.Add(CreateImportedTextureAsset(textureAsset, exportPath));
        return textureAsset.AssetGuid;
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

    private static Texture CreateTextureAsset(
        GltfImportContext context,
        ExportPath exportPath,
        Image image)
    {
        AssetGuid assetGuid = AssetGuid.FromSource(context.SourceMeta.SourceGuid, exportPath.SubAssetKey);
        ImageResult decoded = ImageResult.FromMemory(
            ReadImageBytes(image).ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        byte[] pixels = decoded.Data;
        uint width = checked((uint)decoded.Width);
        uint height = checked((uint)decoded.Height);
        return new Texture
        {
            AssetGuid = assetGuid.ToFlatString(),
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

    private static void SaveTextureAsset(
        GltfImportContext context,
        Texture textureAsset,
        ExportPath exportPath)
    {
        AssetWriter.Write(textureAsset, exportPath.OutputPath);
        GltfDeps.SaveMeta(
            exportPath.OutputPath,
            textureAsset.AssetGuid,
            context.SourceMeta.SourceGuid,
            exportPath.SubAssetKey,
            context.Fingerprint);
    }

    private static ImportedAsset CreateImportedTextureAsset(
        Texture textureAsset,
        ExportPath exportPath)
    {
        return ImportedAsset.Create(textureAsset, exportPath.SubAssetKey, exportPath.OutputPath);
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

    private static void SetTexture(Material asset, string name, string textureGuid)
    {
        uint slot = asset.RequireSlot(name, typeof(Texture));
        if (!AssetGuid.TryParse(textureGuid, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Texture slot '{name}' contains invalid GUID '{textureGuid}'.");
        asset.SetTextureReference(slot, guid);
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
        AssetImportFingerprint Fingerprint)
    {
        public string SourceStem => Path.GetFileNameWithoutExtension(FullSourcePath);
        public string SourceDirectory => Path.GetDirectoryName(FullSourcePath)!;
    }

    private readonly record struct MaterialTemplates(
        Material Lit,
        Material Unlit);

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

