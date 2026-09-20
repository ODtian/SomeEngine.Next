using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Assets;

public sealed partial class SourceMeta
{
    public required SourceGuid SourceGuid { get; init; }
    /// <summary>Persistent importer selections and settings, independent of any one build.</summary>
    public List<SourceImporterSettings> Importers { get; init; } = [];
    public bool UseDefaultImporters { get; init; } = true;
    /// <summary>Persistent source element identities. Keys come from source identity, never display names.</summary>
    public Dictionary<string, Guid> ElementIds { get; init; } = new(StringComparer.Ordinal);
    public SourceBuildRecord Build { get; init; } = new();
    public uint ImporterVersion { get => Build.ImporterVersion; set => Build.ImporterVersion = value; }
    public string InputHash { get => Build.InputHash; set => Build.InputHash = value; }
    public IReadOnlyList<FileDependency> Inputs { get => Build.Inputs; set => Build.Inputs = value; }
    public List<SourceOutput> Outputs { get => Build.Outputs; init => Build.Outputs = value; }

    public T GetSettings<T>(Guid importerId, T defaults, JsonTypeInfo<T> typeInfo)
    {
        SourceImporterSettings? selection = Importers.SingleOrDefault(value => value.ImporterId == importerId);
        return selection is null || selection.Settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? defaults : selection.Settings.Deserialize(typeInfo)
            ?? throw new InvalidDataException($"Importer {importerId} has null settings.");
    }

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
    public Guid Version { get; init; }
    public string ContentHash { get; init; } = "";
    public IReadOnlyList<Guid> AssetDependencies { get; init; } = [];
}

public sealed record SourceImporterSettings(Guid ImporterId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Settings, bool Enabled = true);

/// <summary>Replaceable results of a completed import. These never contain editable source settings.</summary>
public sealed class SourceBuildRecord
{
    public Guid ImporterId { get; set; }
    public uint ImporterVersion { get; set; }
    public string InputHash { get; set; } = "";
    public IReadOnlyList<FileDependency> Inputs { get; set; } = [];
    public IReadOnlyList<FileDependency> ReportedInputs { get; set; } = [];
    public List<SourceOutput> Outputs { get; set; } = [];

    internal static SourceBuildRecord Read(ReadOnlySpan<byte> bytes)
    {
        SourceBuildRecord? value;
        try { value = JsonSerializer.Deserialize(bytes, SourceMetaJsonContext.Default.SourceBuildRecord); }
        catch (JsonException) { return new(); }
        if (value?.InputHash is null || value.Inputs is null || value.ReportedInputs is null || value.Outputs is null
            || value.Inputs.Concat(value.ReportedInputs).Any(static input => input is null
                || input.RelativePath is null || input.ContentHash is null || input.RelativePath.Contains('\0'))
            || value.Outputs.Any(static output => output is null || output.SubAssetKey is null
                || output.InputHash is null || output.AssetPath is null || output.AssetPath.Contains('\0')
                || output.ContentHash is null || output.Dependencies is null || output.AssetDependencies is null))
            return new();
        return value;
    }
}

public sealed partial class SourceMeta
{
    public static SourceMeta GetOrCreate(string sourcePath)
    {
        if (File.Exists(GetMetaPath(sourcePath))) return Load(sourcePath);
        var meta = new SourceMeta { SourceGuid = SourceGuid.New() };
        Save(sourcePath, meta);
        return meta;
    }

    public static SourceMeta Load(string sourcePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        SourceMetaDocument data = JsonSerializer.Deserialize(File.ReadAllText(GetMetaPath(sourcePath)),
            SourceMetaJsonContext.Default.SourceMetaDocument)
            ?? throw new InvalidDataException($"Invalid source metadata '{sourcePath}'.");
        SourceBuildRecord build = File.Exists(GetBuildPath(sourcePath))
            ? SourceBuildRecord.Read(File.ReadAllBytes(GetBuildPath(sourcePath))) : new();
        build.Outputs = build.Outputs.Select(output => output with
        {
            AssetPath = Path.GetFullPath(Path.Combine(directory, output.AssetPath)),
        }).ToList();
        var result = new SourceMeta
        {
            SourceGuid = SourceGuid.Parse(data.SourceGuid),
            Importers = data.Importers, UseDefaultImporters = data.UseDefaultImporters,
            ElementIds = data.ElementIds, Build = build,
        };
        return result;
    }

    public static void Save(string sourcePath, SourceMeta meta)
    {
        using var files = new AssetImportFiles();
        try { AddFiles(files, sourcePath, meta); files.Commit(); }
        catch (Exception error) { files.RecordFailure(error); throw; }
    }

    internal static void AddFiles(AssetImportFiles files, string sourcePath, SourceMeta meta)
    {
        foreach (SourceOutput output in meta.Outputs)
            if (!meta.ElementIds.TryAdd(output.SubAssetKey, output.AssetGuid)
                && meta.ElementIds[output.SubAssetKey] != output.AssetGuid)
                throw new InvalidDataException($"Output '{output.SubAssetKey}' changes an existing source element identity.");
        string directory = Path.GetDirectoryName(GetMetaPath(sourcePath))!;
        var data = new SourceMetaDocument
        {
            SourceGuid = meta.SourceGuid.ToString(),
            Importers = meta.Importers, UseDefaultImporters = meta.UseDefaultImporters, ElementIds = meta.ElementIds,
        };
        var build = new SourceBuildRecord
        {
            ImporterId = meta.Build.ImporterId, ImporterVersion = meta.ImporterVersion, InputHash = meta.InputHash, Inputs = meta.Inputs,
            ReportedInputs = meta.Build.ReportedInputs,
            Outputs = meta.Outputs.Select(output => output with
            {
                AssetPath = Path.GetRelativePath(directory, output.AssetPath).Replace('\\', '/'),
            }).ToList(),
        };
        files.AddWrite(GetMetaPath(sourcePath), JsonSerializer.SerializeToUtf8Bytes(data, SourceMetaJsonContext.Default.SourceMetaDocument));
        files.AddWrite(GetBuildPath(sourcePath), JsonSerializer.SerializeToUtf8Bytes(build, SourceMetaJsonContext.Default.SourceBuildRecord));
    }

    public static string GetMetaPath(string sourcePath) => Path.GetFullPath(sourcePath) + ".meta";
    public static string GetBuildPath(string sourcePath) => Path.GetFullPath(sourcePath) + ".import.json";

    internal byte[] EncodeSettings()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteBooleanValue(UseDefaultImporters);
            foreach (SourceImporterSettings settings in Importers.OrderBy(static item => item.ImporterId))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(settings.ImporterId);
                writer.WriteBooleanValue(settings.Enabled);
                if (settings.Settings.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
                else settings.Settings.WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }
}

internal sealed class SourceMetaDocument
{
    public string SourceGuid { get; set; } = "";
    public List<SourceImporterSettings> Importers { get; set; } = [];
    public bool UseDefaultImporters { get; set; } = true;
    public Dictionary<string, Guid> ElementIds { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SourceMetaDocument))]
[JsonSerializable(typeof(SourceBuildRecord))]
internal sealed partial class SourceMetaJsonContext : JsonSerializerContext;
