using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Assets;

public sealed partial class AssetManifest
{
    public const string CatalogFileName = "catalog.json";

    private readonly Dictionary<SourceGuid, string> _sources = [];
    private readonly Dictionary<string, SourceGuid> _sourceGuidsByPath = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<Guid, AssetEntry> _assets = [];
    private readonly Dictionary<string, Guid> _assetGuidsByPath = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private IReadOnlyDictionary<SourceGuid, IReadOnlyList<Guid>>? _assetsBySource;
    private IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? _referencers;

    public IReadOnlyDictionary<SourceGuid, string> Sources => _sources;
    public IReadOnlyDictionary<Guid, AssetEntry> Assets => _assets;
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Dependencies => _assets.ToDictionary(static pair => pair.Key, static pair => pair.Value.Dependencies);

    public void AddSource(SourceGuid guid, string path)
    {
        if (guid.IsEmpty)
        {
            return;
        }

        string normalizedPath = AssetIoHelpers.NormalizePath(path);
        if (_sourceGuidsByPath.TryGetValue(normalizedPath, out SourceGuid otherSource) && otherSource != guid)
            throw new InvalidOperationException($"Source path '{normalizedPath}' is already registered to {otherSource}.");
        if (_sources.TryGetValue(guid, out string? previousPath))
        {
            if (!_sourceGuidsByPath.Comparer.Equals(previousPath, normalizedPath))
                throw new InvalidOperationException($"Source {guid} occurs at both '{previousPath}' and '{normalizedPath}'.");
        }

        _sources[guid] = normalizedPath;
        _sourceGuidsByPath[normalizedPath] = guid;
        _assetsBySource = null;
    }

    public bool RemoveSource(SourceGuid guid)
    {
        if (!_sources.Remove(guid, out string? path))
            return false;
        _sourceGuidsByPath.Remove(path);
        _assetsBySource = null;
        return true;
    }

    public void AddAsset(
        Guid guid,
        string name,
        string path,
        string assetType,
        ulong schemaFingerprint,
        SourceGuid sourceGuid = default,
        string subAssetKey = "",
        IEnumerable<Guid>? dependencies = null,
        string definitionPath = "",
        Guid version = default)
    {
        if ((guid == System.Guid.Empty))
        {
            throw new ArgumentException("Asset guid cannot be empty.", nameof(guid));
        }
        if (schemaFingerprint == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaFingerprint),
                "An asset manifest entry requires an exact schema fingerprint.");
        }

        string normalizedPath = AssetIoHelpers.NormalizePath(path);
        if (_assetGuidsByPath.TryGetValue(normalizedPath, out Guid existingGuidForPath)
            && existingGuidForPath != guid)
        {
            throw new InvalidOperationException($"Asset path '{normalizedPath}' is already registered to {existingGuidForPath}.");
        }

        if (_assets.TryGetValue(guid, out AssetEntry existing))
        {
            _assetGuidsByPath.Remove(existing.Path);
        }

        _assets[guid] = new AssetEntry(
            guid,
            name ?? string.Empty,
            normalizedPath,
            assetType ?? string.Empty,
            schemaFingerprint,
            sourceGuid,
            subAssetKey ?? string.Empty)
        {
            Dependencies = NormalizeDependencies(dependencies),
            DefinitionPath = AssetIoHelpers.NormalizePath(definitionPath),
            Version = version,
        };
        _assetGuidsByPath[normalizedPath] = guid;
        _assetsBySource = null;
        _referencers = null;
    }

    public bool RemoveAsset(Guid guid)
    {
        if (!_assets.Remove(guid, out AssetEntry record))
            return false;
        _assetGuidsByPath.Remove(record.Path);
        _assetsBySource = null;
        _referencers = null;
        return true;
    }

    public bool TrySourcePath(SourceGuid guid, out string path) => _sources.TryGetValue(guid, out path!);
    public bool TryGetAsset(Guid guid, out AssetEntry record) => _assets.TryGetValue(guid, out record);
    public bool TrySourceGuid(string path, out SourceGuid guid) => _sourceGuidsByPath.TryGetValue(AssetIoHelpers.NormalizePath(path), out guid);

    public bool TryAssetPath(string path, out AssetEntry record)
    {
        if (_assetGuidsByPath.TryGetValue(AssetIoHelpers.NormalizePath(path), out Guid guid) && _assets.TryGetValue(guid, out record))
        {
            return true;
        }

        record = default;
        return false;
    }

    public bool TrySourceAsset(SourceGuid sourceGuid, string subAssetKey, out AssetEntry record)
    {
        EnsureIndexes();
        record = default;
        if (sourceGuid.IsEmpty || string.IsNullOrWhiteSpace(subAssetKey) || !_assetsBySource!.TryGetValue(sourceGuid, out IReadOnlyList<Guid>? assetGuids))
        {
            return false;
        }

        foreach (Guid guid in assetGuids)
        {
            if (_assets.TryGetValue(guid, out AssetEntry asset) && string.Equals(asset.SubAssetKey, subAssetKey, StringComparison.Ordinal))
            {
                record = asset;
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<Guid> AssetsBySource(SourceGuid sourceGuid)
    {
        EnsureIndexes();
        return _assetsBySource!.TryGetValue(sourceGuid, out IReadOnlyList<Guid>? values) ? values : [];
    }

    public IReadOnlyList<Guid> GetDependencies(Guid guid) => _assets.TryGetValue(guid, out AssetEntry entry) ? entry.Dependencies : [];

    public IReadOnlyList<Guid> GetReferencers(Guid guid)
    {
        EnsureIndexes();
        return _referencers!.TryGetValue(guid, out IReadOnlyList<Guid>? values) ? values : [];
    }

    public IReadOnlyList<AssetEntry> List(string? assetType = null)
        => _assets.Values
            .Where(record => string.IsNullOrWhiteSpace(assetType) || string.Equals(record.AssetType, assetType, StringComparison.Ordinal))
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ThenBy(static record => record.AssetGuid.ToString(), StringComparer.Ordinal)
            .ToArray();

    public void Save(string manifestDirectory)
    {
        using var files = new AssetImportFiles();
        try { AddFiles(files, manifestDirectory); files.Commit(); }
        catch (Exception error) { files.RecordFailure(error); throw; }
    }

    internal AssetManifest Copy()
    {
        var copy = new AssetManifest();
        foreach ((SourceGuid id, string path) in _sources) copy.AddSource(id, path);
        foreach (AssetEntry entry in _assets.Values)
            copy.AddAsset(entry.AssetGuid, entry.Name, entry.Path, entry.AssetType, entry.SchemaFingerprint,
                entry.SourceGuid, entry.SubAssetKey, entry.Dependencies, entry.DefinitionPath, entry.Version);
        return copy;
    }

    internal void AddFiles(AssetImportFiles files, string manifestDirectory)
    {
        manifestDirectory = Path.GetFullPath(manifestDirectory);
        var catalog = new AssetCatalogDocument
        {
            Sources = CreateSourceIndex(), Assets = CreateAssetIndex(), Dependencies = CreateDependencyGraph(),
        };
        // Sources, outputs and dependencies become visible together after all output files are saved.
        files.AddWrite(Path.Combine(manifestDirectory, CatalogFileName),
            JsonSerializer.SerializeToUtf8Bytes(catalog, AssetManifestJsonContext.Default.AssetCatalogDocument));
    }

    public static AssetManifest Load(string manifestDirectory)
    {
        manifestDirectory = Path.GetFullPath(manifestDirectory);
        AssetManifest manifest = new();
        string path = Path.Combine(manifestDirectory, CatalogFileName);
        // Replacement may proceed while this handle continues reading the preceding complete file.
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return manifest; }
        catch (DirectoryNotFoundException) { return manifest; }
        using var read = stream;
        AssetCatalogDocument catalog = JsonSerializer.Deserialize(stream, AssetManifestJsonContext.Default.AssetCatalogDocument)
            ?? throw new InvalidDataException("The asset catalog is empty.");
        SourceIndexDocument sourceIndex = catalog.Sources;
        foreach (SourceEntryDoc entry in sourceIndex.Sources)
        {
            if (SourceGuid.TryParse(entry.SourceGuid, out SourceGuid guid))
            {
                manifest.AddSource(guid, entry.Path);
            }
        }

        Dictionary<Guid, IReadOnlyList<Guid>> dependencies = ReadDependencies(catalog.Dependencies);

        AssetIndexDocument assetIndex = catalog.Assets;
        foreach (AssetEntryDoc entry in assetIndex.Assets)
        {
            if (!Guid.TryParse(entry.AssetGuid, out Guid guid))
            {
                continue;
            }

            SourceGuid sourceGuid = SourceGuid.TryParse(entry.SourceGuid, out SourceGuid parsedSourceGuid) ? parsedSourceGuid : SourceGuid.Empty;
            manifest.AddAsset(
                guid,
                entry.Name,
                entry.Path,
                entry.AssetType,
                ParseSchemaFingerprint(entry),
                sourceGuid,
                entry.SubAssetKey,
                dependencies.TryGetValue(guid, out IReadOnlyList<Guid>? values) ? values : [], entry.DefinitionPath, entry.Version);
        }

        return manifest;
    }

    private SourceIndexDocument CreateSourceIndex()
        => new()
        {
            Sources = _sources
                .OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(static pair => new SourceEntryDoc
                {
                    SourceGuid = pair.Key.ToString(),
                    Path = pair.Value,
                })
                .ToList(),
        };

    private AssetIndexDocument CreateAssetIndex()
        => new()
        {
            Assets = _assets.Values
                .OrderBy(static record => record.AssetGuid.ToString(), StringComparer.Ordinal)
                .Select(CreateAssetEntry)
                .ToList(),
        };

    private DependencyGraphDocument CreateDependencyGraph()
        => new()
        {
            Assets = Dependencies
                .OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(static pair => new DepEntryDoc
                {
                    AssetGuid = pair.Key.ToString(),
                    Dependencies = pair.Value.Select(static guid => guid.ToString()).ToList(),
                })
                .ToList(),
        };

    private static AssetEntryDoc CreateAssetEntry(AssetEntry record)
        => new()
        {
            AssetGuid = record.AssetGuid.ToString(),
            Name = record.Name,
            Path = record.Path,
            AssetType = record.AssetType,
            SchemaFingerprint = record.SchemaFingerprint.ToString("X16", CultureInfo.InvariantCulture),
            SourceGuid = record.SourceGuid.IsEmpty ? string.Empty : record.SourceGuid.ToString(),
            SubAssetKey = record.SubAssetKey,
            DefinitionPath = record.DefinitionPath,
            Version = record.Version,
        };

    private static Dictionary<Guid, IReadOnlyList<Guid>> ReadDependencies(DependencyGraphDocument graph)
    {
        Dictionary<Guid, IReadOnlyList<Guid>> dependencies = [];
        foreach (DepEntryDoc entry in graph.Assets)
        {
            if (Guid.TryParse(entry.AssetGuid, out Guid guid))
            {
                dependencies[guid] = ParseDependencies(entry);
            }
        }

        return dependencies;
    }

    private static ulong ParseSchemaFingerprint(AssetEntryDoc entry)
    {
        if (!ulong.TryParse(
                entry.SchemaFingerprint,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong value)
            || value == 0)
        {
            throw new InvalidDataException(
                $"Asset manifest entry '{entry.Path}' has no current exact schema fingerprint.");
        }
        return value;
    }

    private static IReadOnlyList<Guid> ParseDependencies(DepEntryDoc entry)
        => entry.Dependencies
            .Select(static value => Guid.TryParse(value, out Guid dependency) ? dependency : Guid.Empty)
            .Where(static dependency => (dependency != System.Guid.Empty))
            .Distinct()
            .OrderBy(static dependency => dependency.ToString(), StringComparer.Ordinal)
            .ToArray();

    private void EnsureIndexes()
    {
        if (_assetsBySource != null && _referencers != null)
        {
            return;
        }

        Dictionary<SourceGuid, List<Guid>> assetsBySource = [];
        foreach (AssetEntry record in _assets.Values)
        {
            if (record.SourceGuid.IsEmpty)
            {
                continue;
            }

            if (!assetsBySource.TryGetValue(record.SourceGuid, out List<Guid>? values))
            {
                values = [];
                assetsBySource[record.SourceGuid] = values;
            }

            values.Add(record.AssetGuid);
        }

        Dictionary<Guid, HashSet<Guid>> referencers = _assets.Keys.ToDictionary(static guid => guid, static _ => new HashSet<Guid>());
        foreach ((Guid owner, IReadOnlyList<Guid> dependencies) in Dependencies)
        {
            foreach (Guid dependency in dependencies)
            {
                if (!referencers.TryGetValue(dependency, out HashSet<Guid>? values))
                {
                    values = [];
                    referencers[dependency] = values;
                }

                values.Add(owner);
            }
        }

        _assetsBySource = assetsBySource.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<Guid>)pair.Value.OrderBy(static guid => guid.ToString(), StringComparer.Ordinal).ToArray());
        _referencers = referencers.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<Guid>)pair.Value.OrderBy(static guid => guid.ToString(), StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<Guid> NormalizeDependencies(IEnumerable<Guid>? dependencies)
        => dependencies?
            .Where(static dependency => dependency != System.Guid.Empty)
            .Distinct()
            .OrderBy(static dependency => dependency.ToString(), StringComparer.Ordinal)
            .ToArray() ?? [];

    private sealed class SourceIndexDocument
    {
        public List<SourceEntryDoc> Sources { get; set; } = [];
    }

    private sealed class SourceEntryDoc
    {
        public string SourceGuid { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed class AssetIndexDocument
    {
        public List<AssetEntryDoc> Assets { get; set; } = [];
    }

    private sealed class AssetEntryDoc
    {
        public string AssetGuid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string AssetType { get; set; } = string.Empty;
        public string SchemaFingerprint { get; set; } = string.Empty;
        public string SourceGuid { get; set; } = string.Empty;
        public string SubAssetKey { get; set; } = string.Empty;
        public string DefinitionPath { get; set; } = string.Empty;
        public Guid Version { get; set; }
    }

    private sealed class AssetCatalogDocument
    {
        public SourceIndexDocument Sources { get; set; } = new();
        public AssetIndexDocument Assets { get; set; } = new();
        public DependencyGraphDocument Dependencies { get; set; } = new();
    }

    private sealed class DependencyGraphDocument
    {
        public List<DepEntryDoc> Assets { get; set; } = [];
    }

    private sealed class DepEntryDoc
    {
        public string AssetGuid { get; set; } = string.Empty;
        public List<string> Dependencies { get; set; } = [];
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(SourceIndexDocument))]
    [JsonSerializable(typeof(AssetIndexDocument))]
    [JsonSerializable(typeof(DependencyGraphDocument))]
    [JsonSerializable(typeof(AssetCatalogDocument))]
    private sealed partial class AssetManifestJsonContext : JsonSerializerContext
    {
    }
}

