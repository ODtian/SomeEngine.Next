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

    public Guid CreateAsset<T>(string assetPath, Asset<T> asset) where T : class
    {
        lock (_gate)
        {
            string fullPath = FullPath(assetPath);
            Guid id = asset.Id.Value;
            if (Resolve(assetPath) is Guid registered && registered != id)
                throw new InvalidOperationException($"Asset '{assetPath}' is registered as '{registered}', not '{id}'.");
            AssetWriter.Write(asset, fullPath);
            RegisterAssetFile(fullPath, asset.File.Entry);
            return id;
        }
    }

    // ── Import: 源文件 → .asset 文件 → manifest 注册 ──
    public async ValueTask<IReadOnlyList<Guid>> ImportAsync(
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
    public async ValueTask<Guid> RegisterAssetAsync<T>(string assetPath,
        CancellationToken cancellationToken = default) where T : class
    {
        string fullPath = FullPath(assetPath);
        await using Asset<T> asset = await AssetLoader.OpenFileAsync<T>(fullPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_gate)
            RegisterAssetFile(fullPath, asset.File.Entry);
        return asset.Id.Value;
    }

    // ── Resolve / Query ──
    public Guid? Resolve(string sourcePath, string? subAssetKey = null)
    {
        lock (_gate)
        {
            string manifestPath = ToManifestPath(sourcePath);
            if (Manifest.TryAssetPath(manifestPath, out AssetEntry assetRecord))
            {
                return assetRecord.AssetGuid;
            }

            if (!Manifest.TrySourceGuid(manifestPath, out SourceGuid sourceGuid))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(subAssetKey))
            {
                return Manifest.TrySourceAsset(sourceGuid, subAssetKey, out AssetEntry record)
                    ? record.AssetGuid
                    : null;
            }

            IReadOnlyList<Guid> assets = Manifest.AssetsBySource(sourceGuid);
            return assets.Count == 1 ? assets[0] : null;
        }
    }

    public IReadOnlyList<AssetEntry> List(string? assetType = null)
    {
        lock (_gate)
            return Manifest.List(assetType);
    }

    public IReadOnlyList<Guid> GetDependencies(Guid guid)
    {
        lock (_gate)
            return Manifest.GetDependencies(guid);
    }

    public IReadOnlyList<Guid> GetReferencers(Guid guid)
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

    private async ValueTask<IReadOnlyList<Guid>> ImportWithImporterAsync(
        string fullPath,
        IAssetImporter importer,
        CancellationToken cancellationToken)
    {
        SourceMeta sourceMeta = SourceMeta.GetOrCreate(fullPath, importer.ImporterName);
        ImportInputs? inputs = importer.GetInputs(_projectRoot, fullPath, sourceMeta);
        if (inputs is not null && sourceMeta.ImporterVersion == inputs.ImporterVersion &&
            sourceMeta.InputHash == inputs.InputHash && sourceMeta.Outputs.Count != 0 &&
            sourceMeta.Outputs.All(output => File.Exists(output.AssetPath) && Manifest.Assets.ContainsKey(output.AssetGuid)))
            return sourceMeta.Outputs.Select(static output => output.AssetGuid).ToArray();
        IReadOnlyList<ImportedAsset> importedAssets = await importer
            .ImportAsync(_projectRoot, fullPath, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            // 直接注册进 manifest，不扫描
            Manifest.AddSource(sourceMeta.SourceGuid, ToManifestPath(fullPath));
            var outputIds = importedAssets.Select(static output => output.AssetGuid).ToHashSet();
            foreach (Guid previous in Manifest.AssetsBySource(sourceMeta.SourceGuid).ToArray())
                if (!outputIds.Contains(previous)) Manifest.RemoveAsset(previous);
            List<Guid> result = [];
            foreach (ImportedAsset imported in importedAssets)
            {
                if ((imported.AssetGuid == System.Guid.Empty))
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
        foreach ((Guid assetGuid, AssetEntry record) in Manifest.Assets)
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
        foreach ((Guid ownerGuid, IReadOnlyList<Guid> dependencies) in Manifest.Dependencies)
        {
            if (!Manifest.TryGetAsset(ownerGuid, out AssetEntry owner))
            {
                continue;
            }

            AddMissingDependencyDiagnostics(diagnostics, ownerGuid, owner, dependencies);
        }
    }

    private void AddMissingDependencyDiagnostics(
        List<AssetDiagnostic> diagnostics,
        Guid ownerGuid,
        AssetEntry owner,
        IReadOnlyList<Guid> dependencies)
    {
        foreach (Guid dependency in dependencies)
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
        AssetEntry info,
        SourceGuid sourceGuid = default,
        string subAssetKey = "",
        bool saveManifest = true)
    {
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
