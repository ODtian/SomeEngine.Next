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
                await using AssetPack pack = await AssetPack.OpenAsync(outputPath, cancellationToken: cancellationToken);
                if (pack.Count == entries.Length && ContainsVersions(pack, entries)) return false;
            }

            var builder = new AssetPackBuilder();
            foreach (AssetEntry entry in entries)
            {
                byte[] bytes = await File.ReadAllBytesAsync(FullPath(entry.Path), cancellationToken);
                builder.AddAsset(entry.AssetGuid, entry.AssetType, bytes, entry.SchemaFingerprint);
            }
            await builder.PublishAsync(outputPath, cancellationToken: cancellationToken);
            return true;
        }
        finally { _importGate.Release(); }
    }

    private static bool ContainsVersions(AssetPack pack, AssetEntry[] entries)
    {
        foreach (AssetEntry entry in entries)
        {
            if (!pack.TryGetEntry(entry.AssetGuid, out AssetPackEntry? stored) || stored!.DocumentVersion != entry.Version) return false;
        }
        return true;
    }
}
