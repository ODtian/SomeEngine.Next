using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using System.Security.Cryptography;
using System.Text;
using SomeEngine.Job;

namespace SomeEngine.Assets;

public sealed partial class AssetProject
{
    public ValueTask<IReadOnlyList<Guid>> ImportAsync<TSource, TSettings>(string sourcePath,
        IAssetSourceImporter<TSource, TSettings> importer, AssetImportSettings<TSettings>? overrides,
        CancellationToken cancellationToken = default) where TSource : class
        => ImportAsync(sourcePath, new SourceImportOverrides<TSource, TSettings>(importer, overrides), cancellationToken);

    private sealed class SourceImportOverrides<TSource, TSettings>(IAssetSourceImporter<TSource, TSettings> importer,
        AssetImportSettings<TSettings>? overrides) : IAssetSourceImporter<TSource> where TSource : class
    {
        public Guid Id => importer.Id;
        public ValueTask<TSource> ReadAsync(string path, CancellationToken token) => importer.ReadAsync(path, token);
        public ValueTask ImportAsync(TSource source, ImportContext context, CancellationToken token)
            => importer.ImportAsync(source, context, overrides, token);
        public ValueTask<ImportInputs?> GetInputsAsync(string root, string path, SourceMeta metadata, CancellationToken token)
            => importer.GetInputsAsync(root, path, metadata, overrides, token);
    }

    /// <summary>Reads and imports a source, then commits every asset, source record and directory file together.</summary>
    public async ValueTask<IReadOnlyList<Guid>> ImportAsync<TSource>(string sourcePath,
        IAssetSourceImporter<TSource> importer, CancellationToken cancellationToken = default) where TSource : class
    {
        ArgumentNullException.ThrowIfNull(importer);
        Scheduler scheduler = JobSystem.Current;
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string fullPath = RequireProjectPath(_projectRoot, sourcePath);
            byte[]? originalMetadata = ReadOptional(SourceMeta.GetMetaPath(fullPath));
            byte[]? originalBuild = ReadOptional(SourceMeta.GetBuildPath(fullPath));
            SourceMeta metadata = originalMetadata is null
                ? new SourceMeta { SourceGuid = SourceGuid.New() }
                : SourceMeta.Load(fullPath);
            ImportInputs? inputs = await importer.GetInputsAsync(_projectRoot, fullPath, metadata, cancellationToken).ConfigureAwait(false);
            if (inputs is not null) inputs = IncludeSettings(inputs, metadata, importer.Id);
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileDependency>? previousInputs = inputs is null ? null
                : await RefreshReportedInputsAsync(metadata.Build.ReportedInputs, cancellationToken).ConfigureAwait(false);
            if (inputs is not null && previousInputs is not null
                && metadata.InputHash == IncludeReportedInputs(inputs, previousInputs).InputHash && metadata.ImporterVersion == inputs.ImporterVersion
                && metadata.Build.ImporterId == importer.Id && await IsCacheValidAsync(metadata, cancellationToken).ConfigureAwait(false))
                return metadata.Outputs.Select(static output => output.AssetGuid).ToArray();

            TSource source = await importer.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await using var sourceDisposal = new SourceDisposal(source);
            await using var context = new ImportContext(_projectRoot, fullPath, metadata, cancellationToken, scheduler);
            await importer.ImportAsync(source, context, cancellationToken).ConfigureAwait(false);

            files = new AssetImportFiles();
            var entries = new List<AssetEntry>();
            var outputHashes = new Dictionary<Guid, string>();
            foreach (ImportContext.AssetOutput output in context.Outputs)
            {
                bool registered;
                lock (_gate) registered = Manifest.TryGetAsset(output.Asset.Id, out AssetEntry previous)
                    && previous.SourceGuid == metadata.SourceGuid && PathsEqual(FullPath(previous.Path), output.Path);
                await CheckOutputFileAsync(output.Path, output.Asset.Id, cancellationToken, allowCorrupt: registered).ConfigureAwait(false);
                string stage = files.CreateStagePath(output.Path);
                files.AddFile(output.Path, stage);
                AssetEntry entry = await output.Asset.SaveAsync(stage, cancellationToken).ConfigureAwait(false);
                if (entry.AssetGuid != output.Asset.Id || !PathsEqual(Path.GetFullPath(entry.Path), stage))
                    throw new InvalidDataException("The saved draft identity or path differs from the requested output.");
                entries.Add(entry with { Path = output.Path, SourceGuid = metadata.SourceGuid, SubAssetKey = output.ElementKey });
                outputHashes[entry.AssetGuid] = await HashFileAsync(stage, cancellationToken).ConfigureAwait(false);
            }

            var outputIds = entries.Select(static entry => entry.AssetGuid).ToHashSet();
            AssetEntry[] removed;
            lock (_gate) removed = Manifest.AssetsBySource(metadata.SourceGuid)
                .Select(id => Manifest.Assets[id]).Where(entry => !outputIds.Contains(entry.AssetGuid)
                    || !entries.Any(output => output.AssetGuid == entry.AssetGuid
                        && PathsEqual(output.Path, FullPath(entry.Path)))).ToArray();
            foreach (AssetEntry old in removed)
            {
                string oldPath = RequireProjectPath(_projectRoot, old.Path);
                if (entries.Any(entry => PathsEqual(entry.Path, oldPath))) continue;
                await CheckOutputFileAsync(oldPath, old.AssetGuid, cancellationToken, allowCorrupt: true).ConfigureAwait(false);
                files.AddDelete(oldPath);
            }

            if (inputs is not null)
            {
                ImportInputs? currentInputs = await importer.GetInputsAsync(_projectRoot, fullPath, metadata, cancellationToken).ConfigureAwait(false);
                if (currentInputs is null || IncludeSettings(currentInputs, metadata, importer.Id).InputHash != inputs.InputHash)
                    throw new IOException("Import inputs changed while converting the source.");
            }
            var reported = new List<FileDependency>();
            foreach ((string path, string hash) in context.ReportedInputs())
            {
                if (await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != hash)
                    throw new IOException($"Importer input changed after it was read: '{path}'.");
                reported.Add(new FileDependency { RelativePath = Path.GetRelativePath(_projectRoot, path).Replace('\\', '/'), ContentHash = hash });
            }
            if (inputs is not null) inputs = IncludeReportedInputs(inputs, reported);
            metadata.Build.ImporterId = importer.Id;
            metadata.Build.ImporterVersion = inputs?.ImporterVersion ?? 0;
            metadata.Build.InputHash = inputs?.InputHash ?? "";
            metadata.Build.Inputs = inputs?.Dependencies ?? [];
            metadata.Build.ReportedInputs = reported;
            metadata.Build.Outputs = entries.Select(entry => new SourceOutput
            {
                AssetGuid = entry.AssetGuid, SubAssetKey = entry.SubAssetKey, AssetPath = entry.Path,
                InputHash = metadata.InputHash, Dependencies = [],
                Version = entry.Version, ContentHash = outputHashes[entry.AssetGuid],
                AssetDependencies = entry.Dependencies,
            }).ToList();
            lock (_gate)
            {
                CheckUnchanged(SourceMeta.GetMetaPath(fullPath), originalMetadata);
                CheckUnchanged(SourceMeta.GetBuildPath(fullPath), originalBuild);
                AssetManifest next = Manifest.Copy();
                next.AddSource(metadata.SourceGuid, ToManifestPath(fullPath));
                foreach (Guid id in next.AssetsBySource(metadata.SourceGuid).ToArray()) next.RemoveAsset(id);
                foreach (AssetEntry entry in entries)
                {
                    if (Manifest.TryGetAsset(entry.AssetGuid, out AssetEntry previous) && previous.SourceGuid != metadata.SourceGuid)
                        throw new InvalidOperationException($"Asset {entry.AssetGuid} belongs to another source.");
                    next.AddAsset(entry.AssetGuid, entry.Name, ToManifestPath(entry.Path), entry.AssetType, entry.SchemaFingerprint,
                        metadata.SourceGuid, entry.SubAssetKey, entry.Dependencies, version: entry.Version);
                }
                SourceMeta.AddFiles(files, fullPath, metadata);
                next.AddFiles(files, _manifestDirectory);
                files.Commit(cancellationToken);
                Manifest = next;
            }
            return entries.Select(static entry => entry.AssetGuid).ToArray();
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally
        {
            try { files?.Dispose(); }
            finally { _importGate.Release(); }
        }
    }

    private static byte[]? ReadOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private async ValueTask<IReadOnlyList<FileDependency>?> RefreshReportedInputsAsync(IReadOnlyList<FileDependency> previous, CancellationToken token)
    {
        var result = new List<FileDependency>(previous.Count);
        foreach (FileDependency input in previous)
        {
            try { result.Add(new FileDependency { RelativePath = input.RelativePath, ContentHash = await HashFileAsync(FullPath(input.RelativePath), token).ConfigureAwait(false) }); }
            catch (IOException) { return null; }
        }
        return result;
    }

    private static ImportInputs IncludeReportedInputs(ImportInputs inputs, IReadOnlyList<FileDependency> reported)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(inputs.InputHash));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var dependencies = inputs.Dependencies.ToDictionary(static input => input.RelativePath, comparer);
        foreach (FileDependency input in reported.OrderBy(static input => input.RelativePath, comparer))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{input.RelativePath.Length}:{input.RelativePath}{input.ContentHash}"));
            if (dependencies.TryGetValue(input.RelativePath, out FileDependency? existing)
                && !StringComparer.OrdinalIgnoreCase.Equals(existing.ContentHash, input.ContentHash))
                throw new IOException($"Importers observed different bytes for '{input.RelativePath}'.");
            dependencies[input.RelativePath] = input;
        }
        return new ImportInputs { InputHash = Convert.ToHexString(hash.GetHashAndReset()), ImporterVersion = inputs.ImporterVersion,
            Dependencies = dependencies.Values.OrderBy(static input => input.RelativePath, comparer).ToArray() };
    }

    private async ValueTask<bool> IsCacheValidAsync(SourceMeta metadata, CancellationToken cancellationToken)
    {
        foreach (SourceOutput output in metadata.Outputs)
        {
            lock (_gate)
                if (output.Version == Guid.Empty || output.ContentHash.Length == 0
                    || !Manifest.TryGetAsset(output.AssetGuid, out AssetEntry entry)
                    || entry.SourceGuid != metadata.SourceGuid || entry.Version != output.Version
                    || !PathsEqual(FullPath(entry.Path), output.AssetPath)) return false;
            try
            {
                if (await HashFileAsync(output.AssetPath, cancellationToken).ConfigureAwait(false) != output.ContentHash) return false;
                await using var source = FileRangeSource.Open(output.AssetPath);
                if (await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false) != output.Version) return false;
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
        }
        return true;
    }

    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }

    private static ImportInputs IncludeSettings(ImportInputs inputs, SourceMeta metadata, Guid importerId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(importerId.ToByteArray());
        hash.AppendData(Encoding.UTF8.GetBytes(inputs.InputHash));
        hash.AppendData(metadata.EncodeSettings());
        return new ImportInputs { InputHash = Convert.ToHexString(hash.GetHashAndReset()),
            ImporterVersion = inputs.ImporterVersion, Dependencies = inputs.Dependencies };
    }

    private sealed class SourceDisposal(object source) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (source is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (source is IDisposable disposable) disposable.Dispose();
        }
    }

    private static void CheckUnchanged(string path, byte[]? previous)
    {
        byte[]? current = ReadOptional(path);
        if (previous is null ? current is not null : current is null || !previous.AsSpan().SequenceEqual(current))
            throw new IOException($"Source metadata changed during import: '{path}'.");
    }

    private static async ValueTask CheckOutputFileAsync(string path, Guid id, CancellationToken cancellationToken, bool allowCorrupt = false)
    {
        if (!File.Exists(path)) return;
        Guid actual;
        try
        {
            await using var source = FileRangeSource.Open(path);
            var metadata = await BinaryDocument.InspectAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using BinaryDocument document = await BinaryDocument.OpenAsync(source,
                new(metadata.TypeId, metadata.SchemaFingerprint, metadata.Compatibility, metadata.SchemaEpoch),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            actual = document.CreateRootReader().ReadGuid();
        }
        catch (Exception error) when (allowCorrupt && error is InvalidDataException or EndOfStreamException) { return; }
        if (actual != id)
            throw new InvalidDataException($"Output file '{path}' belongs to another asset.");
    }
}
