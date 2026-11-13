using System.Text.Json;
using System.Text.RegularExpressions;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Importers;

internal readonly record struct ShaderInputAsset(Guid Id, string DefinitionPath, string[] Sources);

internal static partial class ShaderImports
{
    [GeneratedRegex("(?m)^\\s*(?:__exported\\s+)?import\\s+(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_./]*))\\s*;")]
    private static partial Regex ImportDeclaration();

    [GeneratedRegex("(?m)^\\s*#include\\s+\"([^\"]+)\"")]
    private static partial Regex IncludeDeclaration();

    internal static ShaderInputAsset[] Read(ShaderSourceBlock source, string definitionPath,
        IReadOnlyList<string>? includeDirectories)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(definitionPath))!;
        string[] searchPaths = [directory, .. (includeDirectories ?? []).Select(path => Path.GetFullPath(path, directory))];
        var inputs = new Dictionary<Guid, ShaderInputAsset>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in source.SourceNames)
            if (FindFile(name, directory, searchPaths) is { } path) ReadFile(path);
        if (MaterialShaderEntries.DeclaresMaterial(source, definitionPath, includeDirectories))
        {
            foreach (string name in MaterialShaderEntries.SharedInputNames)
                if (FindFile(name + ".slang", directory, searchPaths) is { } path) AddInput(path);
            foreach (string name in new[] { MaterialShaderEntries.RasterEntriesHeader, MaterialShaderEntries.TessellationEntriesHeader })
                if (FindFile(name, directory, searchPaths) is { } path) ReadFile(path);
        }
        return inputs.Values.OrderBy(input => input.DefinitionPath, StringComparer.Ordinal).ToArray();

        void ReadFile(string path)
        {
            if (!visited.Add(path)) return;
            string text = File.ReadAllText(path);
            string currentDirectory = Path.GetDirectoryName(path)!;
            foreach (Match include in IncludeDeclaration().Matches(text))
                if (FindFile(include.Groups[1].Value, currentDirectory, searchPaths) is { } header) ReadFile(header);
            foreach (Match import in ImportDeclaration().Matches(text))
            {
                string name = import.Groups[1].Success ? import.Groups[1].Value : import.Groups[2].Value;
                string fileName = name.EndsWith(".slang", StringComparison.Ordinal) ? name : name + ".slang";
                if (FindFile(fileName, currentDirectory, searchPaths) is not { } input) continue;
                AddInput(input);
            }
        }

        void AddInput(string input)
        {
            string definition = Path.ChangeExtension(input, ".shader");
            if (Path.GetFullPath(definition).Equals(Path.GetFullPath(definitionPath), StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(definition)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(definition));
            JsonElement root = document.RootElement;
            JsonElement fields = root.GetProperty("Blocks").EnumerateArray()
                .Select(block => block.GetProperty("Fields"))
                .First(fields => fields.TryGetProperty("SourceNames", out _));
            Guid id = root.GetProperty("Fields").GetProperty("Id").GetGuid();
            string[] sources = fields.GetProperty("SourceNames").EnumerateArray()
                .Select(value => Path.GetFullPath(value.GetString()!, Path.GetDirectoryName(definition)!)).ToArray();
            inputs.TryAdd(id, new(id, definition, sources));
        }
    }

    internal static HashSet<string> ReadSourcePaths(IEnumerable<ShaderInputAsset> inputs,
        IReadOnlyList<string>? includeDirectories)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Guid>();
        foreach (ShaderInputAsset input in inputs) Add(input);
        return paths;

        void Add(ShaderInputAsset input)
        {
            if (!visited.Add(input.Id)) return;
            foreach (string path in input.Sources) paths.Add(path);
            using var source = new ShaderSourceBlock
            {
                SourceNames = input.Sources.Select(path => Path.GetRelativePath(Path.GetDirectoryName(input.DefinitionPath)!, path)).ToArray(),
                SourceOnly = true,
            };
            foreach (ShaderInputAsset dependency in Read(source, input.DefinitionPath, includeDirectories)) Add(dependency);
        }
    }

    internal static string ReadVersions(IEnumerable<ShaderInputAsset> inputs)
    {
        return string.Join("|", inputs.Select(input =>
        {
            string path = input.DefinitionPath + ".asset";
            if (!File.Exists(path)) return input.Id + ":" + Guid.Empty;
            FileRangeSource source = FileRangeSource.Open(path);
            try
            {
                Guid version = BinaryDocument.ReadVersionAsync(source).AsTask().GetAwaiter().GetResult();
                return input.Id + ":" + version;
            }
            finally { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }));
    }

    internal static string? FindFile(string name, string directory, IReadOnlyList<string> searchPaths)
    {
        string path = Path.GetFullPath(name, directory);
        if (File.Exists(path)) return path;
        foreach (string search in searchPaths)
        {
            path = Path.GetFullPath(name, Path.GetFullPath(search, directory));
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
