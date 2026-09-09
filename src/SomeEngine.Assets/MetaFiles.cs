using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Assets;

public sealed partial class SourceMeta
{
    public required SourceGuid SourceGuid { get; init; }
    public required string Importer { get; init; }
    public JsonElement? ImporterSettings { get; init; }
    public uint ImporterVersion { get; set; }
    public string InputHash { get; set; } = "";
    public IReadOnlyList<FileDependency> Inputs { get; set; } = [];
    public List<SourceOutput> Outputs { get; init; } = [];

    public SourceOutput? FindOutput(string path)
        => Outputs.FirstOrDefault(output => string.Equals(output.AssetPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    public void SetOutput(SourceOutput output)
    {
        output = output with
        {
            Dependencies = output.Dependencies.Where(dependency => !Inputs.Any(common =>
                common.RelativePath == dependency.RelativePath && common.ContentHash == dependency.ContentHash)).ToArray(),
        };
        int index = Outputs.FindIndex(value => value.SubAssetKey == output.SubAssetKey);
        if (index < 0) Outputs.Add(output); else Outputs[index] = output;
    }
}

public sealed record SourceOutput
{
    public required Guid AssetGuid { get; init; }
    public required string SubAssetKey { get; init; }
    public required string InputHash { get; init; }
    /// <summary>Inputs additional to, or different from, the source-wide Inputs list.</summary>
    public required IReadOnlyList<FileDependency> Dependencies { get; init; }
    public required string AssetPath { get; init; }
}

public sealed partial class SourceMeta
{
    public static SourceMeta GetOrCreate(string sourcePath, string importer = "SlangShaderImporter")
    {
        if (File.Exists(GetMetaPath(sourcePath))) return Load(sourcePath);
        var meta = new SourceMeta { SourceGuid = SourceGuid.New(), Importer = importer };
        Save(sourcePath, meta);
        return meta;
    }

    public static SourceMeta Load(string sourcePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        SourceMetaDocument data = JsonSerializer.Deserialize(File.ReadAllText(GetMetaPath(sourcePath)),
            SourceMetaJsonContext.Default.SourceMetaDocument)
            ?? throw new InvalidDataException($"Invalid source metadata '{sourcePath}'.");
        return new SourceMeta
        {
            SourceGuid = SourceGuid.Parse(data.SourceGuid), Importer = data.Importer,
            ImporterSettings = data.ImporterSettings, ImporterVersion = data.ImporterVersion,
            InputHash = data.InputHash, Inputs = data.Inputs,
            Outputs = data.Outputs.Select(output => output with
            {
                AssetPath = Path.GetFullPath(Path.Combine(directory, output.AssetPath)),
            }).ToList(),
        };
    }

    public static void Save(string sourcePath, SourceMeta meta)
    {
        string metaPath = GetMetaPath(sourcePath);
        string directory = Path.GetDirectoryName(metaPath)!;
        Directory.CreateDirectory(directory);
        var data = new SourceMetaDocument
        {
            SourceGuid = meta.SourceGuid.ToString(), Importer = meta.Importer,
            ImporterSettings = meta.ImporterSettings, ImporterVersion = meta.ImporterVersion,
            InputHash = meta.InputHash, Inputs = meta.Inputs.ToList(),
            Outputs = meta.Outputs.Select(output => output with
            {
                AssetPath = Path.GetRelativePath(directory, output.AssetPath).Replace('\\', '/'),
            }).ToList(),
        };
        string temporary = metaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, SourceMetaJsonContext.Default.SourceMetaDocument));
            File.Move(temporary, metaPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string GetMetaPath(string sourcePath) => Path.GetFullPath(sourcePath) + ".meta";
}

internal sealed class SourceMetaDocument
{
    public string SourceGuid { get; set; } = "";
    public string Importer { get; set; } = "";
    public JsonElement? ImporterSettings { get; set; }
    public uint ImporterVersion { get; set; }
    public string InputHash { get; set; } = "";
    public List<FileDependency> Inputs { get; set; } = [];
    public List<SourceOutput> Outputs { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SourceMetaDocument))]
internal sealed partial class SourceMetaJsonContext : JsonSerializerContext;
