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

    public ImportInputs? GetInputs(string projectRoot, string sourcePath, SourceMeta sourceMeta)
    {
        string fullPath = Path.IsPathRooted(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, fullPath);
        string outputPath = Path.ChangeExtension(fullPath, ".shader.asset");
        SourceOutput? existingAsset = sourceMeta.FindOutput(outputPath);
        if (existingAsset == null)
        {
            return null;
        }

        return SlangDeps.Refresh(
                sourceMeta.Inputs.Concat(existingAsset.Dependencies).DistinctBy(static input => input.RelativePath).ToArray(),
                projectRoot,
                SlangShaderImporter.ImporterVersion,
                profile.FingerprintPart)
            ?? SlangDeps.Refresh(
                sourceMeta.Inputs.Concat(existingAsset.Dependencies).DistinctBy(static input => input.RelativePath).ToArray(),
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
        SourceMeta sourceMeta = SourceMeta.GetOrCreate(fullPath, ImporterName);
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, fullPath);
        string outputPath = Path.ChangeExtension(fullPath, ".shader.asset");
        ShaderData asset = SlangShaderImporter.Import(
            fullPath,
            sourceMeta,
            sourceMeta.FindOutput(outputPath),
            profile);
        IReadOnlyList<ImportedAsset> result =
            Guid.TryParse(asset.AssetGuid, out Guid guid) && (guid != System.Guid.Empty)
                ? [ImportedAsset.Create(asset.ToAsset(), "shader:main", outputPath)]
                : [];
        return ValueTask.FromResult(result);
    }

    private static SlangShaderCookProfile ResolveProfile(SourceMeta sourceMeta, string fullPath)
    {
        SlangShaderImporterSettings settings = SlangShaderImporterSettings.Load(sourceMeta, fullPath);
        return SlangShaderCookProfiles.Resolve(settings.CookProfile);
    }
}

