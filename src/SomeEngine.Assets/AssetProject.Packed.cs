using System.Buffers;
using System.Security.Cryptography;
using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets;

public sealed partial class AssetProject
{
    private string? ImportedPackPath(SourceMeta metadata)
    {
        string[] previous = metadata.Outputs.Where(static output => output.PackPath.Length != 0)
            .Select(output => RequireProjectPath(_projectRoot, output.PackPath)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (previous.Length > 1) throw new InvalidDataException("One source cannot publish into several imported asset packs.");
        string? path = _importedAssetPackPath ?? previous.FirstOrDefault();
        if (path is null) return null;
        if (!path.EndsWith(".assetpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An imported asset pack path must end with '.assetpack'.");
        return path;
    }

    private async ValueTask<Guid?> StagePackedOutputsAsync(ImportContext context, SourceMeta metadata,
        string packPath, AssetImportFiles files, List<AssetEntry> entries, Dictionary<Guid, string> hashes,
        CancellationToken token)
    {
        HashSet<Guid> replacing;
        lock (_gate)
        {
            if (Manifest.TryAssetPath(ToManifestPath(packPath), out _))
                throw new InvalidDataException("The pack path is already registered as a logical asset output.");
            foreach (Guid id in metadata.ElementIds.Values)
                if (Manifest.TryGetAsset(id, out AssetEntry owner) && owner.SourceGuid != metadata.SourceGuid)
                    throw new InvalidDataException($"Source element {id} belongs to another source or native asset.");
            replacing = Manifest.AssetsBySource(metadata.SourceGuid).ToHashSet();
        }
        replacing.UnionWith(metadata.ElementIds.Values);
        var builder = new AssetPackBuilder();
        Guid? previousGeneration = null;
        if (File.Exists(packPath))
        {
            await using AssetPack previous = await AssetPack.OpenAsync(packPath, cancellationToken: token).ConfigureAwait(false);
            if (previous.HasSignature) throw new NotSupportedException("A signed pack requires signed publication.");
            previousGeneration = previous.Generation;
            foreach (AssetPackEntry packed in previous.Entries)
            {
                if (replacing.Contains(packed.AssetId)) continue;
                await using IRangeSource source = await previous.OpenAssetSourceAsync(packed.AssetId, token).ConfigureAwait(false);
                byte[] bytes = new byte[checked((int)source.Length)];
                await source.ReadExactlyAsync(0, bytes, token).ConfigureAwait(false);
                if (!await previous.ContentEqualsAsync(packed.AssetId, packed.AssetType, bytes, packed.SchemaFingerprint, token).ConfigureAwait(false))
                    throw new InvalidDataException($"Unselected packed asset {packed.AssetId} is corrupt.");
                builder.AddAsset(packed.AssetId, packed.AssetType, bytes, packed.SchemaFingerprint);
            }
        }
        foreach (ImportContext.AssetOutput output in context.Outputs)
        {
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            AssetEntry written = await output.Asset.WriteAsync(stream, token).ConfigureAwait(false);
            byte[] bytes = stream.ToArray();
            await using var source = new MemoryRangeSource(bytes);
            AssetEntry actual = await DescribeSourceAsync(source, output.Path, token).ConfigureAwait(false);
            if (written.AssetGuid != output.Asset.Id || written.Path.Length != 0
                || actual.AssetGuid != written.AssetGuid || actual.AssetType != written.AssetType
                || actual.SchemaFingerprint != written.SchemaFingerprint || actual.Version != written.Version)
                throw new InvalidDataException("The written draft identity, type or version differs from the requested output.");
            builder.AddAsset(actual.AssetGuid, actual.AssetType, bytes, actual.SchemaFingerprint);
            entries.Add(actual with { SourceGuid = metadata.SourceGuid, SubAssetKey = output.ElementKey, PackPath = packPath });
            hashes.Add(actual.AssetGuid, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        string stage = files.CreateStagePath(packPath);
        files.AddFinalFile(packPath, stage);
        await using (var destination = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await builder.WriteAsync(destination, cancellationToken: token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        return previousGeneration;
    }

    private static async ValueTask CheckPackUnchangedAsync(string path, Guid? previousGeneration, CancellationToken token)
    {
        if (previousGeneration is null)
        {
            if (File.Exists(path)) throw new IOException("The imported pack appeared during conversion.");
            return;
        }
        await using AssetPack current = await AssetPack.OpenAsync(path, cancellationToken: token).ConfigureAwait(false);
        if (current.Generation != previousGeneration) throw new IOException("The imported pack changed during conversion.");
    }

    private static async ValueTask<string> HashSourceAsync(IRangeSource source, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < source.Length;)
            {
                int count = (int)Math.Min(buffer.Length, source.Length - offset);
                await source.ReadExactlyAsync(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                offset += count;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async ValueTask<AssetEntry> DescribeSourceAsync(IRangeSource source, string path, CancellationToken token)
    {
        var metadata = await BinaryDocument.InspectAsync(source, cancellationToken: token).ConfigureAwait(false);
        BinaryDocument document = await BinaryDocument.OpenAsync(source,
            new(metadata.TypeId, metadata.SchemaFingerprint, metadata.Compatibility, metadata.SchemaEpoch),
            cancellationToken: token).ConfigureAwait(false);
        await using var file = new AssetFile(document, path);
        return file.Describe();
    }

    private HashSet<Guid> FindMissingPackedAssets()
    {
        var missing = new HashSet<Guid>();
        foreach (var group in Manifest.Assets.Values.Where(static entry => entry.PackPath.Length != 0)
            .GroupBy(static entry => entry.PackPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            AssetPack? pack = null;
            try
            {
                pack = AssetPack.OpenAsync(RequireProjectPath(_projectRoot, group.Key)).AsTask().GetAwaiter().GetResult();
                foreach (AssetEntry entry in group)
                    if (!pack.TryGetEntry(entry.AssetGuid, out AssetPackEntry? packed)
                        || packed!.AssetType != entry.AssetType || packed.SchemaFingerprint != entry.SchemaFingerprint)
                        missing.Add(entry.AssetGuid);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            { foreach (AssetEntry entry in group) missing.Add(entry.AssetGuid); }
            finally { pack?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        return missing;
    }

    private async ValueTask DiscoverPackedFilesAsync(string directory, List<AssetDiscoveredFile> discovered, CancellationToken token)
    {
        var candidates = new Dictionary<Guid, (string Path, string Pack)>();
        foreach (AssetEntry entry in Manifest.Assets.Values)
            if (entry.PackPath.Length != 0) Add(entry.AssetGuid, FullPath(entry.Path), entry.PackPath);
        foreach (AssetDiscoveredFile source in discovered.Where(static file => file.Kind == AssetFileKind.ImportedSource))
        {
            if (source.SourceGuid.IsEmpty) continue;
            foreach (SourceOutput output in SourceMeta.Load(source.Path).Outputs)
                if (output.PackPath.Length != 0) Add(output.AssetGuid, output.AssetPath, output.PackPath);
        }
        foreach (var group in candidates.GroupBy(static candidate => candidate.Value.Pack, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!File.Exists(group.Key)) continue;
            await using AssetPack pack = await AssetPack.OpenAsync(group.Key, cancellationToken: token).ConfigureAwait(false);
            foreach (var candidate in group)
            {
                if (!pack.TryGetEntry(candidate.Key, out AssetPackEntry? packed)) continue;
                await using IRangeSource source = await pack.OpenAssetSourceAsync(candidate.Key, token).ConfigureAwait(false);
                AssetEntry entry = await DescribeSourceAsync(source, candidate.Value.Path, token).ConfigureAwait(false);
                if (entry.AssetGuid != candidate.Key || entry.AssetType != packed!.AssetType || entry.SchemaFingerprint != packed.SchemaFingerprint)
                    throw new InvalidDataException("The packed document differs from its catalog entry.");
                discovered.RemoveAll(file => file.Kind == AssetFileKind.BinaryAsset && file.Asset!.Value.AssetGuid == entry.AssetGuid
                    && PathsEqual(file.Path, entry.Path));
                discovered.Add(new(entry.Path, AssetFileKind.BinaryAsset, entry with { PackPath = group.Key }));
            }
        }
        void Add(Guid id, string path, string packPath)
        {
            path = RequireProjectPath(_projectRoot, path);
            if (!IsContained(directory, path)) return;
            packPath = RequireProjectPath(_projectRoot, packPath);
            if (candidates.TryGetValue(id, out var previous)
                && (!PathsEqual(previous.Path, path) || !PathsEqual(previous.Pack, packPath)))
                throw new InvalidDataException($"Asset {id} has conflicting packed output locations.");
            candidates[id] = (path, packPath);
        }
    }
}
