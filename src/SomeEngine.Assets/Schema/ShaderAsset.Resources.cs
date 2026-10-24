using SlangShaderSharp;
using SomeEngine.Core.Collections;

namespace SomeEngine.Assets.Schema;

public sealed partial class ShaderAsset
{
    /// <summary>Reuses a live shader, sharing the complete file read and creation for this version.</summary>
    public static ValueTask<Shader> GetAsync(AssetLoader files, AssetId<ShaderAsset> id,
        WeakValues<AssetResourceKey<ShaderAsset>, Shader> values,
        IGlobalSession slang, SessionDesc settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new AssetResourceKey<ShaderAsset>(files, id, files.GetSourceVersion(id));
        ValueTask<Shader> value = values.GetOrBegin(key, out TaskCompletionSource<Shader>? completion);
        if (completion is not null)
            value = values.CompleteAsync(key, completion, ReadShaderAsync(files, id, slang, settings, cancellationToken));
        return cancellationToken.CanBeCanceled && !value.IsCompletedSuccessfully
            ? new(value.AsTask().WaitAsync(cancellationToken)) : value;
    }

    private static async ValueTask<Shader> ReadShaderAsync(AssetLoader files, AssetId<ShaderAsset> id,
        IGlobalSession slang, SessionDesc settings, CancellationToken cancellationToken)
    {
        await using ShaderAsset asset = await OpenAsync(files, id, cancellationToken).ConfigureAwait(false);
        return await asset.CreateAsync(slang, settings, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
