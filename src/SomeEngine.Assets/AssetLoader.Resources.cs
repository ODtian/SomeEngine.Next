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
        catch (Exception error) { CompleteFailedResourceRead(_shaders, id.Value, read, error); }
    }

    public async ValueTask<Shader> RefreshShaderAsync(AssetId<ShaderAsset> id,
        CancellationToken cancellationToken = default)
    {
        await using ShaderAsset asset = await ShaderAsset.OpenAsync(this, id, cancellationToken).ConfigureAwait(false);
        Shader shader = await asset.CreateAsync(_slang!, _sessionDescription, _specializations,
            cancellationToken).ConfigureAwait(false);
        var read = new TaskCompletionSource<Shader>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.SetResult(shader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _shaders[id.Value] = read;
        }
        return shader;
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
        catch (Exception error) { CompleteFailedResourceRead(_textures, id.Value, read, error); }
    }

    private void CompleteFailedResourceRead<T>(Dictionary<Guid, TaskCompletionSource<T>> reads,
        Guid id, TaskCompletionSource<T> read, Exception error)
    {
        lock (_gate)
        {
            if (reads.TryGetValue(id, out TaskCompletionSource<T>? current) && ReferenceEquals(current, read))
                reads.Remove(id);
            if (error is OperationCanceledException canceled)
                read.TrySetCanceled(canceled.CancellationToken);
            else
            {
                read.TrySetException(error);
                _ = read.Task.Exception;
            }
        }
    }

    public async ValueTask<Texture> RefreshTextureAsync(AssetId<TextureAsset> id,
        CancellationToken cancellationToken = default)
    {
        Texture texture = await GetTextureAsync(id, cancellationToken).ConfigureAwait(false);
        await using TextureAsset asset = await TextureAsset.OpenAsync(this, id, cancellationToken).ConfigureAwait(false);
        TextureBlock pixels = await asset.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
        Texture replacement = await pixels.CreateAsync(asset.Name, cancellationToken).ConfigureAwait(false);
        await texture.UpdateAsync(replacement, cancellationToken).ConfigureAwait(false);
        return texture;
    }
}
