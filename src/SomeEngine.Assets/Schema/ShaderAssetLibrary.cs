using SlangShaderSharp;
using SomeEngine.Resources;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Schema;

/// <summary>Reads named shader assets from one immutable pack through the normal asset loader.</summary>
public sealed class ShaderAssetLibrary : IAsyncDisposable
{
    private readonly AssetPackStorage _storage;
    private readonly AssetLoader _files;
    private readonly Dictionary<string, AssetId<ShaderAsset>> _assets;

    private ShaderAssetLibrary(AssetPackStorage storage, AssetLoader files, Dictionary<string, AssetId<ShaderAsset>> assets)
        => (_storage, _files, _assets) = (storage, files, assets);

    /// <summary>Takes the pack into this library and indexes its shader names.</summary>
    public static async ValueTask<ShaderAssetLibrary> OpenAsync(AssetPack pack, CancellationToken cancellationToken = default)
    {
        var storage = new AssetPackStorage([pack]);
        var files = new AssetLoader(storage);
        var assets = new Dictionary<string, AssetId<ShaderAsset>>(StringComparer.Ordinal);
        foreach (AssetPackEntry entry in pack.Entries)
        {
            var id = new AssetId<ShaderAsset>(entry.AssetId);
            using AssetFile file = await files.OpenFileAsync(id, ShaderAsset.FileType, ShaderAsset.WireType, cancellationToken)
                .ConfigureAwait(false);
            assets.Add(file.Describe().Name, id);
        }
        return new(storage, files, assets);
    }

    /// <summary>Returns resident code and direct Slang reflection for the selected shader.</summary>
    public async ValueTask<Shader> CreateAsync(string name, IGlobalSession global, SessionDesc settings,
        CancellationToken cancellationToken = default)
    {
        using ShaderAsset asset = await ShaderAsset.OpenAsync(_files, _assets[name], cancellationToken).ConfigureAwait(false);
        return await asset.CreateAsync(global, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _files.DisposeAsync().ConfigureAwait(false);
        await _storage.DisposeAsync().ConfigureAwait(false);
    }
}
