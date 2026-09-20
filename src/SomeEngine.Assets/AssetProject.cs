using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Assets;

/// <summary>
/// Imports, creates, indexes and validates project assets without managing rendering residency.
/// </summary>
public sealed partial class AssetProject
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly string _projectRoot;
    private readonly string _manifestDirectory;
    private readonly IReadOnlyList<IAssetSourceImporter> _importers;
    private readonly IReadOnlyList<IAssetSourceFormat> _sourceFormats;

    public AssetProject(
        string projectRoot,
        IEnumerable<IAssetSourceImporter> importers,
        string? manifestDirectory = null,
        IEnumerable<IAssetSourceFormat>? sourceFormats = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestDirectory = RequireProjectPath(_projectRoot, manifestDirectory ?? Path.Combine(_projectRoot, "Library", "AssetManifest"), allowProjectRoot: true);
        _importers = importers?.ToArray() ?? throw new ArgumentNullException(nameof(importers));
        _sourceFormats = sourceFormats?.ToArray() ?? [];
        if (_sourceFormats.Select(format => format.SourceExtension).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _sourceFormats.Count)
            throw new ArgumentException("Each source suffix must select one format.", nameof(sourceFormats));

        Manifest = AssetManifest.Load(_manifestDirectory);
    }

    public string RootPath => _projectRoot;

    public AssetManifest Manifest { get; private set; }

    public LooseAssetStorage CreateStorage()
    {
        lock (_gate)
            return new LooseAssetStorage(_projectRoot, Manifest);
    }

    public Guid RegisterAsset(AssetEntry entry)
    {
        _importGate.Wait();
        try
        {
            lock (_gate)
            {
                string fullPath = RequireProjectPath(_projectRoot, entry.Path);
                if (Resolve(entry.Path) is Guid registered && registered != entry.AssetGuid)
                    throw new InvalidOperationException($"Asset '{entry.Path}' is already registered with a different ID.");
                RegisterAssetFile(fullPath, entry);
                return entry.AssetGuid;
            }
        }
        finally { _importGate.Release(); }
    }

    /// <summary>Builds a native definition and registers only its successfully saved output.</summary>
    public async ValueTask<Guid> BuildAsync(string sourcePath, string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string fullPath = FullPath(sourcePath);
            IAssetSourceFormat format = _sourceFormats.SingleOrDefault(format =>
                fullPath.EndsWith(format.SourceExtension, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException($"No native source format is registered for '{fullPath}'.");
            byte[] sourceBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            AssetEntry description = AssetText.Describe(sourceBytes);
            string targetPath = RequireProjectPath(_projectRoot, outputPath ?? fullPath + ".asset");
            void CheckDestination()
            {
                if (Manifest.TryAssetPath(ToManifestPath(targetPath), out AssetEntry destination) && destination.AssetGuid != description.AssetGuid)
                    throw new InvalidDataException($"The output path is already registered for asset {destination.AssetGuid}.");
                if (Manifest.TryGetAsset(description.AssetGuid, out AssetEntry existing)
                    && (existing.AssetType != description.AssetType || existing.DefinitionPath.Length != 0
                        && !StringComparer.Ordinal.Equals(existing.DefinitionPath, ToManifestPath(fullPath))))
                    throw new InvalidDataException($"Asset {description.AssetGuid} is already defined by '{existing.DefinitionPath}'.");
            }
            lock (_gate) CheckDestination();
            string buildPath = SourceMeta.GetBuildPath(fullPath);
            byte[]? previousBuild = ReadOptional(buildPath);
            if (await CanReuseSourceAsync(format, fullPath, targetPath, description, previousBuild,
                cancellationToken).ConfigureAwait(false)) return description.AssetGuid;
            bool registered;
            lock (_gate) registered = Manifest.TryGetAsset(description.AssetGuid, out AssetEntry previous)
                && PathsEqual(FullPath(previous.Path), targetPath)
                && PathsEqual(FullPath(previous.DefinitionPath), fullPath);
            await CheckOutputFileAsync(targetPath, description.AssetGuid, cancellationToken, allowCorrupt: registered).ConfigureAwait(false);
            files = new AssetImportFiles();
            string stage = files.CreateStagePath(targetPath);
            files.AddFile(targetPath, stage);
            AssetSourceResult result = await format.BuildAsync(_projectRoot, fullPath, stage, cancellationToken).ConfigureAwait(false);
            AssetEntry output = result.Asset;
            if (output.AssetGuid != description.AssetGuid || output.AssetType != description.AssetType
                || !PathsEqual(Path.GetFullPath(output.Path), stage))
                throw new InvalidDataException("The built asset identity, type or path differs from its native definition.");
            CheckUnchanged(fullPath, sourceBytes);
            ImportInputs? finalInputs = await format.GetInputsAsync(_projectRoot, fullPath,
                result.Inputs.Dependencies, cancellationToken).ConfigureAwait(false);
            if (finalInputs is null || finalInputs.InputHash != result.Inputs.InputHash
                || finalInputs.ImporterVersion != result.Inputs.ImporterVersion)
                throw new IOException("Native definition inputs changed during the build.");
            var build = new SourceBuildRecord
            {
                ImporterVersion = result.Inputs.ImporterVersion, InputHash = result.Inputs.InputHash,
                Inputs = result.Inputs.Dependencies,
                Outputs = [new SourceOutput
                {
                    AssetGuid = output.AssetGuid, SubAssetKey = "", InputHash = result.Inputs.InputHash,
                    Dependencies = [], AssetPath = Path.GetRelativePath(Path.GetDirectoryName(fullPath)!, targetPath),
                    Version = output.Version, ContentHash = await HashFileAsync(stage, cancellationToken).ConfigureAwait(false),
                    AssetDependencies = output.Dependencies,
                }],
            };
            files.AddWrite(buildPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(build,
                SourceMetaJsonContext.Default.SourceBuildRecord));
            lock (_gate)
            {
                CheckDestination();
                CheckUnchanged(buildPath, previousBuild);
                AssetManifest next = Manifest.Copy();
                next.AddAsset(output.AssetGuid, output.Name, ToManifestPath(targetPath), output.AssetType,
                    output.SchemaFingerprint, dependencies: output.Dependencies, definitionPath: ToManifestPath(fullPath), version: output.Version);
                next.AddFiles(files, _manifestDirectory);
                files.Commit(cancellationToken);
                Manifest = next;
                return output.AssetGuid;
            }
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally
        {
            try { files?.Dispose(); }
            finally { _importGate.Release(); }
        }
    }

    // ── Import: 源文件 → .asset 文件 → manifest 注册 ──
    public async ValueTask<IReadOnlyList<Guid>> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath;
        IAssetSourceImporter? importer;
        lock (_gate)
        {
            fullPath = RequireProjectPath(_projectRoot, sourcePath);
            importer = MatchImporter(fullPath);
        }

        if (importer is null)
            throw new NotSupportedException($"No source importer is registered for '{fullPath}'.");

        return await importer.ImportAsync(this, fullPath, cancellationToken).ConfigureAwait(false);
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

            foreach (AssetEntry definition in Manifest.Assets.Values)
                if (definition.DefinitionPath == manifestPath) return definition.AssetGuid;

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

    internal static string RequireProjectPath(string projectRoot, string path, bool allowProjectRoot = false)
    {
        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        string relative = Path.GetRelativePath(projectRoot, fullPath);
        if ((!allowProjectRoot && relative == ".") || Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Project output or edit path '{fullPath}' is outside '{projectRoot}'.");
        return fullPath;
    }

    internal static bool PathsEqual(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string ToManifestPath(string path) => AssetIoHelpers.ToManifestPath(_projectRoot, path);

    private IAssetSourceImporter? MatchImporter(string path)
    {
        string normalized = AssetIoHelpers.NormalizePath(path);
        return _importers.SingleOrDefault(importer => importer.MatchesSourcePath(normalized));
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
        AssetManifest destination = saveManifest ? Manifest.Copy() : Manifest;
        destination.AddAsset(
            info.AssetGuid,
            info.Name,
            ToManifestPath(fullPath),
            info.AssetType,
            info.SchemaFingerprint,
            sourceGuid,
            subAssetKey,
            info.Dependencies,
            info.DefinitionPath,
            info.Version);

        if (saveManifest)
        {
            destination.Save(_manifestDirectory);
            Manifest = destination;
        }
    }
}
