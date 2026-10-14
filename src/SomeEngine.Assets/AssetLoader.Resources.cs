using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets;

public sealed partial class AssetLoader
{
    private readonly IGlobalSession? _slang;
    private readonly SessionDesc _sessionDescription;
    private readonly ReadOnlyMemory<SpecializationArg> _specializations;
    private readonly Dictionary<Guid, TaskCompletionSource<Shader>> _shaders = [];
    private readonly Dictionary<Guid, TaskCompletionSource<Texture>> _textures = [];

    public AssetLoader(IAssetStorage storage, IGlobalSession slang, SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default, AssetLoaderOptions? options = null)
        : this(storage, options)
    {
        _slang = slang;
        _sessionDescription = sessionDescription;
        _specializations = specializations;
    }

    public async ValueTask<Shader> GetShaderAsync(AssetId<ShaderAsset> id,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<Shader> read;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_shaders.TryGetValue(id.Value, out read!))
            {
                read = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _shaders.Add(id.Value, read);
                start = true;
            }
        }
        if (start) _ = ReadShaderAsync(id, read);
        return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadShaderAsync(AssetId<ShaderAsset> id, TaskCompletionSource<Shader> read)
    {
        try
        {
            if (_slang is null)
                throw new InvalidOperationException("Shader creation requires Slang settings in the AssetLoader constructor.");
            await using ShaderAsset asset = await ShaderAsset.OpenAsync(this, id, _stop.Token).ConfigureAwait(false);
            Shader shader = await asset.CreateAsync(_slang, _sessionDescription,
                _specializations, _stop.Token).ConfigureAwait(false);
            read.TrySetResult(shader);
        }
        catch (OperationCanceledException canceled) { read.TrySetCanceled(canceled.CancellationToken); }
        catch (Exception error)
        {
            read.TrySetException(error);
            _ = read.Task.Exception;
        }
    }

    public async ValueTask<Texture> GetTextureAsync(AssetId<TextureAsset> id,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<Texture> read;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_textures.TryGetValue(id.Value, out read!))
            {
                read = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _textures.Add(id.Value, read);
                start = true;
            }
        }
        if (start) _ = ReadTextureAsync(id, read);
        return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadTextureAsync(AssetId<TextureAsset> id, TaskCompletionSource<Texture> read)
    {
        try
        {
            await using TextureAsset asset = await TextureAsset.OpenAsync(this, id, _stop.Token).ConfigureAwait(false);
            TextureBlock pixels = await asset.GetBlockAsync<TextureBlock>(_stop.Token).ConfigureAwait(false);
            read.TrySetResult(await pixels.CreateAsync(asset.Name, _stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException canceled) { read.TrySetCanceled(canceled.CancellationToken); }
        catch (Exception error)
        {
            read.TrySetException(error);
            _ = read.Task.Exception;
        }
    }
}
