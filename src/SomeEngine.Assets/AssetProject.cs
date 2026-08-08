using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>
/// Authoring-only asset project. It imports, creates, indexes, and validates assets but never owns
/// the runtime resident table.
/// </summary>
public sealed class AssetProject
{
    private readonly object _gate = new();
    private readonly string _projectRoot;
    private readonly string _manifestDirectory;
    private readonly IReadOnlyList<IAssetImporter> _importers;

    public AssetProject(
        string projectRoot,
        IEnumerable<IAssetImporter> importers,
        string? manifestDirectory = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestDirectory = Path.GetFullPath(manifestDirectory ?? Path.Combine(_projectRoot, "Library", "AssetManifest"));
        _importers = importers?.ToArray() ?? throw new ArgumentNullException(nameof(importers));

        Manifest = File.Exists(Path.Combine(_manifestDirectory, AssetManifest.AssetIndexFileName))
            ? AssetManifest.Load(_manifestDirectory)
            : new AssetManifest();
    }

    public AssetManifest Manifest { get; private set; }

    public LooseAssetStorage CreateStorage()
    {
        lock (_gate)
            return new LooseAssetStorage(_projectRoot, Manifest);
    }

    /// <summary>
    /// Authoring-only typed access to one exact current asset document from a storage backend.
    /// Storage remains a GUID-to-range provider; the shared internal document path performs all
    /// type, schema, publication, and root-identity validation.
    /// </summary>
    public static async ValueTask<BinaryDocument<TAsset>> OpenAsync<TAsset>(
        IAssetStorage storage,
        AssetEntry entry,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        where TAsset : class, IBinaryContract<TAsset>
    {
        ArgumentNullException.ThrowIfNull(storage);
        AssetTypeDescriptor<TAsset> descriptor = AssetType<TAsset>.Descriptor;
        ValidateEntry<TAsset>(entry);

        IRangeSource? source = await storage.OpenAsync(entry, cancellationToken)
            .ConfigureAwait(false);
        BinaryDocument<TAsset>? document = null;
        try
        {
            IRangeSource ownedSource = source;
            source = null;
            document = await BinaryDocument<TAsset>.OpenAsync(
                ownedSource,
                ownsSource: true,
                limits,
                cancellationToken).ConfigureAwait(false);

            if (document.SchemaFingerprint != entry.SchemaFingerprint)
            {
                throw new InvalidDataException(
                    $"Asset {entry.AssetGuid} document fingerprint does not match its storage entry.");
            }
            AssetGuid rootGuid = descriptor.GetAssetGuid(document.Root);
            if (rootGuid != entry.AssetGuid)
            {
                throw new InvalidDataException(
                    $"Asset {entry.AssetGuid} root declares GUID {rootGuid}.");
            }

            BinaryDocument<TAsset> result = document;
            document = null;
            return result;
        }
        finally
        {
            if (document is not null)
                await document.DisposeAsync().ConfigureAwait(false);
            else if (source is not null)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static ValueTask<BinaryDocument<TAsset>> OpenAsync<TAsset>(
        IAssetStorage storage,
        AssetEntry entry,
        CancellationToken cancellationToken)
        where TAsset : class, IBinaryContract<TAsset>
        => OpenAsync<TAsset>(storage, entry, limits: null, cancellationToken);

    /// <summary>Authoring-only typed access to one exact current asset file.</summary>
    public static ValueTask<BinaryDocument<TAsset>> OpenAsync<TAsset>(
        string path,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        where TAsset : class, IBinaryContract<TAsset>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        AssetTypeDescriptor<TAsset> descriptor = AssetType<TAsset>.Descriptor;
        if (!descriptor.MatchesPath(fullPath))
        {
            throw new InvalidDataException(
                $"Asset type '{descriptor.AssetType}' does not accept path '{fullPath}'.");
        }

        FileRangeSource source = FileRangeSource.Open(fullPath);
        return BinaryDocument<TAsset>.OpenAsync(
            source,
            ownsSource: true,
            limits,
            cancellationToken);
    }

    public static ValueTask<BinaryDocument<TAsset>> OpenAsync<TAsset>(
        string path,
        CancellationToken cancellationToken)
        where TAsset : class, IBinaryContract<TAsset>
        => OpenAsync<TAsset>(path, limits: null, cancellationToken);

    /// <summary>
    /// Reads root-only authoring data and closes its document before returning. Data with external
    /// chunks must use <see cref="OpenAsync{TAsset}(string, BinaryReadLimits?, CancellationToken)"/>
    /// so the caller explicitly owns the document and its ranges.
    /// </summary>
    internal static async ValueTask<TAsset> ReadAsync<TAsset>(
        string path,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        where TAsset : class, IBinaryContract<TAsset>
    {
        await using BinaryDocument<TAsset> document = await OpenAsync<TAsset>(
            path,
            limits,
            cancellationToken).ConfigureAwait(false);
        if (document.ChunkCount != 0)
        {
            throw new InvalidOperationException(
                $"Asset '{AssetType<TAsset>.Descriptor.AssetType}' contains external chunks; open its document explicitly.");
        }

        return document.Root;
    }

    internal static ValueTask<TAsset> ReadAsync<TAsset>(
        string path,
        CancellationToken cancellationToken)
        where TAsset : class, IBinaryContract<TAsset>
        => ReadAsync<TAsset>(path, limits: null, cancellationToken);

    private static void ValidateEntry<TAsset>(AssetEntry entry)
        where TAsset : class, IBinaryContract<TAsset>
    {
        AssetTypeDescriptor<TAsset> descriptor = AssetType<TAsset>.Descriptor;
        if (!StringComparer.Ordinal.Equals(entry.AssetType, descriptor.AssetType))
        {
            throw new InvalidDataException(
                $"Asset {entry.AssetGuid} contains '{entry.AssetType}', not '{descriptor.AssetType}'.");
        }
        if (entry.SchemaFingerprint == descriptor.SchemaFingerprint)
            return;

        BinaryWireTypeDescriptor expected = descriptor.WireType;

        throw new BinarySchemaMismatchException(
            typeof(TAsset),
            entry.SchemaFingerprint,
            expected.SchemaFingerprint,
            expected.SchemaEpoch,
            expected.SchemaEpoch,
            expected.Compatibility);
    }

    public AssetGuid CreateAsset<TAsset>(
        string assetPath,
        TAsset asset)
        where TAsset : class
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(asset);
            AssetTypeDescriptor<TAsset> descriptor = AssetType<TAsset>.Descriptor;

            string fullPath = FullPath(assetPath);
            if (!descriptor.MatchesPath(fullPath))
            {
                throw new InvalidDataException(
                    $"Asset type '{descriptor.AssetType}' does not accept path '{fullPath}'.");
            }

            AssetGuid? registeredGuid = Resolve(assetPath);
            AssetGuid guid = descriptor.GetAssetGuid(asset);
            if (!guid.IsEmpty && registeredGuid is AssetGuid existingGuid && existingGuid != guid)
            {
                throw new InvalidOperationException(
                    $"Asset '{assetPath}' is already registered as '{existingGuid}', but its data declares '{guid}'.");
            }

            if (guid.IsEmpty)
            {
                guid = registeredGuid ?? AssetGuid.New();
                descriptor.SetAssetGuid(asset, guid);
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AssetDescription info = AssetWriter.WriteAndDescribe(asset, fullPath);
            RegisterAssetFile(fullPath, info);
            return guid;
        }
    }

    // ── Import: 源文件 → .asset 文件 → manifest 注册 ──
    public async ValueTask<IReadOnlyList<AssetGuid>> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath;
        IAssetImporter? importer;
        lock (_gate)
        {
            fullPath = FullPath(sourcePath);
            importer = MatchImporter(fullPath);
        }

        if (importer is null)
            throw new NotSupportedException($"No source importer is registered for '{fullPath}'.");

        return await ImportWithImporterAsync(
            fullPath,
            importer,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly registers an already encoded asset. The generic contract selects the
    /// one data reader; no extension scan, format probing, fallback, or second encoding is performed.
    /// </summary>
    public async ValueTask<AssetGuid> RegisterAssetAsync<TAsset>(
        string assetPath,
        CancellationToken cancellationToken = default)
        where TAsset : class, SomeEngine.Serialization.IBinaryContract<TAsset>
    {
        string fullPath = FullPath(assetPath);
        AssetTypeDescriptor<TAsset> descriptor = AssetType<TAsset>.Descriptor;
        if (!descriptor.MatchesPath(fullPath))
        {
            throw new InvalidDataException(
                $"Asset type '{descriptor.AssetType}' does not accept path '{fullPath}'.");
        }

        await using BinaryDocument<TAsset> document = await OpenAsync<TAsset>(
            fullPath,
            limits: null,
            cancellationToken).ConfigureAwait(false);
        AssetDescription info = AssetMetadata.Describe(document.Root, fullPath);
        if (info.AssetGuid.IsEmpty)
            throw new InvalidDataException($"Asset '{fullPath}' has no asset GUID.");

        lock (_gate)
            RegisterDirectAsset(fullPath, info);
        return info.AssetGuid;
    }

    // ── Resolve / Query ──
    public AssetGuid? Resolve(string sourcePath, string? subAssetKey = null)
    {
        lock (_gate)
        {
            string manifestPath = ToManifestPath(sourcePath);
            if (Manifest.TryAssetPath(manifestPath, out AssetManifestRecord assetRecord))
            {
                return assetRecord.Guid;
            }

            if (!Manifest.TrySourceGuid(manifestPath, out SourceGuid sourceGuid))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(subAssetKey))
            {
                return Manifest.TrySourceAsset(sourceGuid, subAssetKey, out AssetManifestRecord record)
                    ? record.Guid
                    : null;
            }

            IReadOnlyList<AssetGuid> assets = Manifest.AssetsBySource(sourceGuid);
            return assets.Count == 1 ? assets[0] : null;
        }
    }

    public IReadOnlyList<AssetManifestRecord> List(string? assetType = null)
    {
        lock (_gate)
            return Manifest.List(assetType);
    }

    public IReadOnlyList<AssetGuid> GetDependencies(AssetGuid guid)
    {
        lock (_gate)
            return Manifest.GetDependencies(guid);
    }

    public IReadOnlyList<AssetGuid> GetReferencers(AssetGuid guid)
    {
        lock (_gate)
            return Manifest.GetReferencers(guid);
    }

    public IReadOnlyList<AssetDiagnostic> Validate()
    {
        lock (_gate)
        {
            List<AssetDiagnostic> diagnostics = [];
            AddMissingSourceDiagnostics(diagnostics);
            AddMissingAssetDiagnostics(diagnostics);
            AddDanglingReferenceDiagnostics(diagnostics);
            return OrderDiagnostics(diagnostics);
        }
    }

    // ── Private helpers ──

    private string FullPath(string path)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_projectRoot, path));

    private string ToManifestPath(string path) => AssetIoHelpers.ToManifestPath(_projectRoot, path);

    private IAssetImporter? MatchImporter(string path)
    {
        string normalized = AssetIoHelpers.NormalizePath(path);
        return _importers.FirstOrDefault(importer => importer.MatchesSourcePath(normalized));
    }

    private async ValueTask<IReadOnlyList<AssetGuid>> ImportWithImporterAsync(
        string fullPath,
        IAssetImporter importer,
        CancellationToken cancellationToken)
    {
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(fullPath, importer.ImporterName);
        IReadOnlyList<ImportedAsset> importedAssets = await importer
            .ImportAsync(_projectRoot, fullPath, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            // 直接注册进 manifest，不扫描
            Manifest.AddSource(sourceMeta.SourceGuid, ToManifestPath(fullPath));
            List<AssetGuid> result = [];
            foreach (ImportedAsset imported in importedAssets)
            {
                if (imported.AssetGuid.IsEmpty)
                    continue;

                RegisterImportedData(imported, sourceMeta.SourceGuid);
                result.Add(imported.AssetGuid);
            }

            Manifest.Save(_manifestDirectory);
            return result;
        }
    }

    private void RegisterImportedData(ImportedAsset imported, SourceGuid sourceGuid)
    {
        RegisterAssetFile(
            imported.OutputPath,
            imported.Info,
            sourceGuid,
            imported.SubAssetKey,
            saveManifest: false);
    }

    private void RegisterDirectAsset(string fullPath, AssetDescription info)
    {
        AssetMeta? meta = AssetMetaFiles.TryLoad(fullPath);
        RegisterAssetFile(
            fullPath,
            info,
            meta?.SourceGuid ?? SourceGuid.Empty,
            meta?.SubAssetKey ?? string.Empty);
    }

    private void AddMissingSourceDiagnostics(List<AssetDiagnostic> diagnostics)
    {
        foreach ((SourceGuid sourceGuid, string sourcePath) in Manifest.Sources)
        {
            string fullPath = FullPath(sourcePath);
            if (File.Exists(fullPath))
            {
                continue;
            }

            diagnostics.Add(new AssetDiagnostic
            {
                Kind = AssetDiagnosticKind.OrphanSourceMeta,
                Severity = AssetDiagnosticSeverity.Warning,
                Path = sourcePath,
                Message = $"Source '{sourcePath}' tracked in manifest but file does not exist.",
                SourceGuid = sourceGuid,
            });
        }
    }

    private void AddMissingAssetDiagnostics(List<AssetDiagnostic> diagnostics)
    {
        foreach ((AssetGuid assetGuid, AssetManifestRecord record) in Manifest.Assets)
        {
            string fullPath = FullPath(record.Path);
            if (File.Exists(fullPath))
            {
                continue;
            }

            diagnostics.Add(new AssetDiagnostic
            {
                Kind = AssetDiagnosticKind.MissingAssetFile,
                Severity = AssetDiagnosticSeverity.Error,
                Path = record.Path,
                Message = $"Asset '{record.Path}' tracked in manifest but file does not exist.",
                AssetGuid = assetGuid,
                SourceGuid = record.SourceGuid,
            });
        }
    }

    private void AddDanglingReferenceDiagnostics(List<AssetDiagnostic> diagnostics)
    {
        foreach ((AssetGuid ownerGuid, IReadOnlyList<AssetGuid> dependencies) in Manifest.Dependencies)
        {
            if (!Manifest.TryGetAsset(ownerGuid, out AssetManifestRecord owner))
            {
                continue;
            }

            AddMissingDependencyDiagnostics(diagnostics, ownerGuid, owner, dependencies);
        }
    }

    private void AddMissingDependencyDiagnostics(
        List<AssetDiagnostic> diagnostics,
        AssetGuid ownerGuid,
        AssetManifestRecord owner,
        IReadOnlyList<AssetGuid> dependencies)
    {
        foreach (AssetGuid dependency in dependencies)
        {
            if (Manifest.Assets.ContainsKey(dependency))
            {
                continue;
            }

            diagnostics.Add(new AssetDiagnostic
            {
                Kind = AssetDiagnosticKind.DanglingReference,
                Severity = AssetDiagnosticSeverity.Warning,
                Path = owner.Path,
                Message = $"Asset '{owner.Path}' references missing asset guid '{dependency}'.",
                AssetGuid = ownerGuid,
                SourceGuid = owner.SourceGuid,
                RelatedAssetGuid = dependency,
            });
        }
    }

    private static IReadOnlyList<AssetDiagnostic> OrderDiagnostics(List<AssetDiagnostic> diagnostics)
        => diagnostics
            .OrderBy(static diagnostic => diagnostic.Kind)
            .ThenBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.AssetGuid.ToString(), StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.RelatedAssetGuid.ToString(), StringComparer.Ordinal)
            .ToArray();

    private void RegisterAssetFile(
        string fullPath,
        AssetDescription info,
        SourceGuid sourceGuid = default,
        string subAssetKey = "",
        bool saveManifest = true)
    {
        AssetMeta? meta = AssetMetaFiles.TryLoad(fullPath);
        if (meta != null && meta.AssetGuid != info.AssetGuid)
        {
            throw new InvalidOperationException(
                $"Asset meta guid '{meta.AssetGuid}' does not match data guid '{info.AssetGuid}' for '{fullPath}'.");
        }

        if (meta != null && !sourceGuid.IsEmpty && meta.SourceGuid != sourceGuid)
        {
            throw new InvalidOperationException(
                $"Asset meta source guid '{meta.SourceGuid}' does not match registration source guid '{sourceGuid}' for '{fullPath}'.");
        }

        Manifest.AddAsset(
            info.AssetGuid,
            info.Name,
            ToManifestPath(fullPath),
            info.AssetType,
            info.SchemaFingerprint,
            sourceGuid,
            subAssetKey,
            info.Dependencies);

        if (saveManifest)
        {
            Manifest.Save(_manifestDirectory);
        }
    }
}
