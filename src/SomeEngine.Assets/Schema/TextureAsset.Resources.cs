using SomeEngine.Core.Collections;

namespace SomeEngine.Assets.Schema;

public sealed partial class TextureAsset
{
    /// <summary>Reuses a live texture, sharing the complete file read and creation for this version.</summary>
    public static ValueTask<Texture> GetAsync(AssetLoader files, AssetId<TextureAsset> id,
        WeakValues<AssetResourceKey<TextureAsset>, Texture> values,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new AssetResourceKey<TextureAsset>(files, id, files.GetSourceVersion(id));
        ValueTask<Texture> value = values.GetOrBegin(key, out TaskCompletionSource<Texture>? completion);
        if (completion is not null)
            value = values.CompleteAsync(key, completion, ReadTextureAsync(files, id, cancellationToken));
        return cancellationToken.CanBeCanceled && !value.IsCompletedSuccessfully
            ? new(value.AsTask().WaitAsync(cancellationToken)) : value;
    }

    private static async ValueTask<Texture> ReadTextureAsync(AssetLoader files, AssetId<TextureAsset> id,
        CancellationToken cancellationToken)
    {
        await using TextureAsset asset = await OpenAsync(files, id, cancellationToken).ConfigureAwait(false);
        TextureBlock pixels = await asset.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
        return await pixels.CreateAsync(asset.Name, cancellationToken).ConfigureAwait(false);
    }
}
