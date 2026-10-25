using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets;

public sealed partial class AssetProject
{
    /// <summary>Builds the selected registered assets, reusing a pack with the same document versions.</summary>
    /// <returns>True when the pack was written.</returns>
    public async ValueTask<bool> BuildPackAsync(string path, IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssetEntry[] entries;
            lock (_gate) entries = assetIds.Select(id => Manifest.Assets[id]).ToArray();
            string outputPath = FullPath(path);
            if (File.Exists(outputPath))
            {
                await using AssetPack pack = await AssetPack.OpenAsync(outputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (pack.Count == entries.Length && ContainsVersions(pack, entries)) return false;
                if (pack.HasSignature) throw new NotSupportedException("A signed pack requires signed publication.");
            }

            var builder = new AssetPackBuilder();
            foreach (AssetEntry entry in entries.Where(static entry => entry.PackPath.Length == 0))
            {
                byte[] bytes = await File.ReadAllBytesAsync(FullPath(entry.Path), cancellationToken).ConfigureAwait(false);
                await using var source = new MemoryRangeSource(bytes);
                if (await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false) != entry.Version)
                    throw new IOException($"The selected asset {entry.AssetGuid} version has been replaced.");
                builder.AddAsset(entry.AssetGuid, entry.AssetType, bytes, entry.SchemaFingerprint);
            }
            foreach (var group in entries.Where(static entry => entry.PackPath.Length != 0)
                .GroupBy(entry => FullPath(entry.PackPath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                await using AssetPack pack = await AssetPack.OpenAsync(group.Key, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (AssetEntry entry in group)
                {
                    if (!pack.TryGetEntry(entry.AssetGuid, out AssetPackEntry? packed)
                        || packed!.AssetType != entry.AssetType || packed.SchemaFingerprint != entry.SchemaFingerprint)
                        throw new InvalidDataException($"Asset {entry.AssetGuid} does not match its pack entry.");
                    await using IRangeSource source = await pack.OpenAssetSourceAsync(entry.AssetGuid, cancellationToken).ConfigureAwait(false);
                    if (await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false) != entry.Version)
                        throw new IOException($"The selected packed asset {entry.AssetGuid} version has been replaced.");
                    byte[] bytes = new byte[checked((int)source.Length)];
                    await source.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
                    if (!await pack.ContentEqualsAsync(entry.AssetGuid, entry.AssetType, bytes, entry.SchemaFingerprint, cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException($"Packed asset {entry.AssetGuid} is corrupt.");
                    builder.AddAsset(entry.AssetGuid, entry.AssetType, bytes, entry.SchemaFingerprint);
                }
            }
            await builder.PublishAsync(outputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _importGate.Release(); }
    }

    private static bool ContainsVersions(AssetPack pack, AssetEntry[] entries)
    {
        foreach (AssetEntry entry in entries)
        {
            if (!pack.TryGetEntry(entry.AssetGuid, out AssetPackEntry? stored)
                || stored!.AssetType != entry.AssetType || stored.SchemaFingerprint != entry.SchemaFingerprint) return false;
            if (stored.DocumentVersion != entry.Version) return false;
        }
        return true;
    }
}
