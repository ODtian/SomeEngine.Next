using System.Text.Json;

namespace SomeEngine.Assets.Importers;

internal static class GltfDeps
{
    public static ImportInputs? ReadInputs(
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        GltfImporterSettings settings)
    {
        List<FileDependency> dependencies = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Add(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        if (!AddProject(projectRoot, settings.LitMaterialTemplate, dependencies, seen)
            || !AddProject(projectRoot, settings.UnlitMaterialTemplate, dependencies, seen))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(fullSourcePath), ".gltf", StringComparison.OrdinalIgnoreCase)
            && !AddUris(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        string settingsFingerprint = sourceMeta.ImporterSettings?.GetRawText() ?? string.Empty;
        return ImportHash.Create(dependencies, GltfSourceImporter.ImporterVersion, settingsFingerprint);
    }

    public static void SaveMeta(
        string outputPath,
        string? assetGuid,
        SourceMeta sourceMeta,
        string sourcePath,
        string subAssetKey,
        ImportInputs fingerprint)
    {
        if (!Guid.TryParse(assetGuid, out Guid parsedGuid) || (parsedGuid == System.Guid.Empty))
        {
            return;
        }

        sourceMeta.Inputs = fingerprint.Dependencies;
        sourceMeta.SetOutput(new SourceOutput
        {
            AssetGuid = parsedGuid, SubAssetKey = subAssetKey,
            InputHash = fingerprint.InputHash, Dependencies = fingerprint.Dependencies,
            AssetPath = Path.GetFullPath(outputPath),
        });
        SourceMeta.Save(sourcePath, sourceMeta);
    }

    public static string FullPath(string projectRoot, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));

    private static bool AddProject(
        string projectRoot,
        string projectRelativePath,
        List<FileDependency> dependencies,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath))
        {
            return false;
        }

        return Add(projectRoot, FullPath(projectRoot, projectRelativePath), dependencies, seen);
    }

    private static bool Add(
        string projectRoot,
        string fullPath,
        List<FileDependency> dependencies,
        HashSet<string> seen)
    {
        FileDependency? dependency = ImportHash.TryFileDep(projectRoot, fullPath);
        if (dependency == null)
        {
            return false;
        }

        if (seen.Add(dependency.RelativePath))
        {
            dependencies.Add(dependency);
        }

        return true;
    }

    private static bool AddUris(
        string projectRoot,
        string fullSourcePath,
        List<FileDependency> dependencies,
        HashSet<string> seen)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullSourcePath));
        JsonElement root = document.RootElement;
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;

        return AddUriArray(projectRoot, sourceDirectory, root, "buffers", dependencies, seen)
            && AddUriArray(projectRoot, sourceDirectory, root, "images", dependencies, seen);
    }

    private static bool AddUriArray(
        string projectRoot,
        string sourceDirectory,
        JsonElement root,
        string propertyName,
        List<FileDependency> dependencies,
        HashSet<string> seen)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("uri", out JsonElement uriElement)
                || uriElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dependencyPath = Uri.UnescapeDataString(uri);
            if (!Add(projectRoot, Path.Combine(sourceDirectory, dependencyPath), dependencies, seen))
            {
                return false;
            }
        }

        return true;
    }
}

