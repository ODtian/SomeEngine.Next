using System;
using System.Collections.Generic;
using System.IO;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public sealed class SlangSourceImporter : IAssetImporter
{
    private static readonly string[] Extensions = [".slang"];

    public string ImporterName => nameof(SlangShaderImporter);
    public IReadOnlyList<string> SourceExtensions => Extensions;

    public bool MatchesSourcePath(string sourcePath)
        => SourceExtensions.Any(extension => sourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public AssetImportFingerprint? GetFingerprint(string projectRoot, string sourcePath, SourceMeta sourceMeta)
    {
        string fullPath = Path.IsPathRooted(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, fullPath);
        string outputPath = Path.ChangeExtension(fullPath, ".shader.asset");
        AssetMeta? existingAsset = AssetMetaFiles.TryLoad(outputPath);
        if (existingAsset == null)
        {
            return null;
        }

        return SlangDeps.Refresh(
                existingAsset.Dependencies,
                projectRoot,
                SlangShaderImporter.ImporterVersion,
                profile.FingerprintPart)
            ?? SlangDeps.Refresh(
                existingAsset.Dependencies,
                Path.GetDirectoryName(fullPath) ?? projectRoot,
                SlangShaderImporter.ImporterVersion,
                profile.FingerprintPart);
    }

    public ValueTask<IReadOnlyList<ImportedAsset>> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.IsPathRooted(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(fullPath, ImporterName);
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, fullPath);
        string outputPath = Path.ChangeExtension(fullPath, ".shader.asset");
        Shader asset = SlangShaderImporter.Import(
            fullPath,
            sourceMeta,
            AssetMetaFiles.TryLoad(outputPath),
            profile);
        IReadOnlyList<ImportedAsset> result =
            AssetGuid.TryParse(asset.AssetGuid, out AssetGuid guid) && !guid.IsEmpty
                ? [ImportedAsset.Create(asset, "shader:main", outputPath)]
                : [];
        return ValueTask.FromResult(result);
    }

    private static SlangShaderCookProfile ResolveProfile(SourceMeta sourceMeta, string fullPath)
    {
        SlangShaderImporterSettings settings = SlangShaderImporterSettings.Load(sourceMeta, fullPath);
        return SlangShaderCookProfiles.Resolve(settings.CookProfile);
    }
}

