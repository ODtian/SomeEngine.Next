using System.Text.Json;
using System.Buffers.Binary;

using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
namespace SomeEngine.Assets.Importers;

internal static class GltfDeps
{
    public static ImportInputs? ReadInputs(
        string projectRoot,
        string fullSourcePath,
        GltfImporterSettings settings, string implementationVersion, string overrides, bool includeMaterialTemplates)
    {
        List<FileDependency> dependencies = [];
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!Add(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        if (includeMaterialTemplates && (!AddProject(projectRoot, settings.LitMaterialTemplate, dependencies, seen)
            || !AddProject(projectRoot, settings.UnlitMaterialTemplate, dependencies, seen)))
        {
            return null;
        }

        if (!AddUris(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        string encodedSettings = JsonSerializer.Serialize(settings, GltfSettingsJson.Default.GltfImporterSettings);
        return ImportHash.Create(dependencies, GltfSourceImporter.ImporterVersion, encodedSettings, implementationVersion, overrides);
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
        using JsonDocument document = ReadJson(fullSourcePath);
        JsonElement root = document.RootElement;
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;

        return AddUriArray(projectRoot, sourceDirectory, root, "buffers", dependencies, seen)
            && AddUriArray(projectRoot, sourceDirectory, root, "images", dependencies, seen);
    }

    private static JsonDocument ReadJson(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) return JsonDocument.Parse(stream);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546C67
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != 0x4E4F534A)
            throw new InvalidDataException("The glTF binary container does not begin with a version 2 JSON chunk.");
        byte[] json = new byte[checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]))];
        stream.ReadExactly(json);
        return JsonDocument.Parse(json);
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

