using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

/// <summary>
/// Keeps device-independent Slang objects resident while the runtime changes scenes.
/// Objects are reused only when their persisted asset versions match.
/// </summary>
internal sealed class RuntimeShaderResources
{
    private readonly record struct ConfigurationIdentity(
        Guid Asset,
        Guid Version,
        string ShaderVersions,
        AssetManifest? Manifest);

    private readonly record struct ShaderFile(
        Guid Asset,
        Guid Version,
        AssetManifest? Manifest);

    private readonly record struct ShaderData(ShaderFile File, Shader? Shader, long ReadNumber);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ShaderData> _shaders = [];
    private readonly Dictionary<ShaderFile, Lazy<Task<Shader>>> _shaderReads = [];
    private readonly Dictionary<int, IGlobalSession> _slang = [];
    private SlangCompileTarget? _target;
    private ClusterShaders? _cluster;
    private ConfigurationIdentity _clusterIdentity;
    private bool _hasClusterIdentity;
    private VirtualShadowMapShaders? _shadows;
    private ConfigurationIdentity _shadowIdentity;
    private bool _hasShadowIdentity;

    internal IGlobalSession GetSlang(SlangCompileTarget target, int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        lock (_gate)
        {
            if (_target is not null && _target != target)
            {
                throw new InvalidOperationException(
                    "Changing the runtime graphics backend requires restarting the application.");
            }
            if (_slang.TryGetValue(channel, out IGlobalSession? existing))
                return existing;

            SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession slang);
            if (result.Failed || slang is null)
                throw new InvalidOperationException($"Slang global session creation failed with {result}.");
            _target = target;
            _slang.Add(channel, slang);
            return slang;
        }
    }

    internal async ValueTask<ClusterShaders> LoadClusterAsync(
        AssetManifest manifest,
        AssetId<ClusterShadersAsset> id,
        Func<CancellationToken, ValueTask<ClusterShaders>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(read);
        ConfigurationIdentity identity = Configuration(
            manifest,
            id.Value,
            ClusterShadersAsset.FileType);
        lock (_gate)
        {
            if (_cluster is not null && _hasClusterIdentity && _clusterIdentity == identity)
                return _cluster;
        }

        ClusterShaders replacement = await read(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _cluster = replacement;
            _clusterIdentity = identity;
            _hasClusterIdentity = true;
        }
        return replacement;
    }

    internal async ValueTask<VirtualShadowMapShaders> LoadShadowsAsync(
        AssetManifest manifest,
        AssetId<VirtualShadowMapShadersAsset> id,
        Func<CancellationToken, ValueTask<VirtualShadowMapShaders>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(read);
        ConfigurationIdentity identity = Configuration(
            manifest,
            id.Value,
            VirtualShadowMapShadersAsset.FileType);
        lock (_gate)
        {
            if (_shadows is not null && _hasShadowIdentity && _shadowIdentity == identity)
                return _shadows;
        }

        VirtualShadowMapShaders replacement = await read(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _shadows = replacement;
            _shadowIdentity = identity;
            _hasShadowIdentity = true;
        }
        return replacement;
    }

    internal ValueTask<Shader> LoadShaderAsync(
        AssetManifest manifest,
        AssetLoader loader,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        AssetId<ShaderAsset> id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(slang);
        AssetEntry entry = Require(manifest, id.Value, ShaderAsset.FileType);
        var file = new ShaderFile(
            id.Value,
            entry.Version,
            entry.Version == Guid.Empty ? manifest : null);
        Lazy<Task<Shader>> read;
        lock (_gate)
        {
            _shaders.TryGetValue(file.Asset, out ShaderData data);
            if (data.Shader is not null && data.File == file)
                return ValueTask.FromResult(data.Shader);
            if (!_shaderReads.TryGetValue(file, out read!))
            {
                if (!_slang.Values.Contains(slang))
                    throw new ArgumentException("The Slang session does not belong to this runtime.", nameof(slang));
                long readNumber = data.ReadNumber + 1;
                _shaders[file.Asset] = data with { ReadNumber = readNumber };
                read = new Lazy<Task<Shader>>(
                    () => ReadShaderAsync(file, loader, id, slang, sessionDescription, readNumber),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _shaderReads.Add(file, read);
            }
        }
        return new ValueTask<Shader>(read.Value.WaitAsync(cancellationToken));
    }

    private async Task<Shader> ReadShaderAsync(
        ShaderFile key,
        AssetLoader loader,
        AssetId<ShaderAsset> id,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        long readNumber)
    {
        try
        {
            using ShaderAsset file = await ShaderAsset.OpenAsync(loader, id).ConfigureAwait(false);
            ShaderSourceBlock source = await file.GetBlockAsync<ShaderSourceBlock>().ConfigureAwait(false);
            Shader shader;
            if (file.ContainsBlock<ShaderBlock>())
            {
                ShaderBlock code = await file.GetBlockAsync<ShaderBlock>().ConfigureAwait(false);
                shader = await code.CreateAsync(source, slang, sessionDescription, file.Name).ConfigureAwait(false);
            }
            else shader = await source.CreateAsync(slang, sessionDescription, file.Name).ConfigureAwait(false);
            lock (_gate)
            {
                if (_shaders[key.Asset].ReadNumber == readNumber)
                    _shaders[key.Asset] = new(key, shader, readNumber);
                _shaderReads.Remove(key);
            }
            return shader;
        }
        catch
        {
            lock (_gate)
                _shaderReads.Remove(key);
            throw;
        }
    }

    private static ConfigurationIdentity Configuration(
        AssetManifest manifest,
        Guid id,
        string assetType)
    {
        AssetEntry configuration = Require(manifest, id, assetType);
        var text = new StringBuilder();
        bool versionsAvailable = configuration.Version != Guid.Empty;
        foreach (AssetEntry shader in manifest.Assets.Values
                     .Where(static entry => entry.AssetType == ShaderAsset.FileType)
                     .OrderBy(static entry => entry.AssetGuid))
        {
            versionsAvailable &= shader.Version != Guid.Empty;
            text.Append(shader.AssetGuid.ToString("N"))
                .Append(':')
                .Append(shader.Version.ToString("N"))
                .Append(';');
        }
        return new ConfigurationIdentity(
            id,
            configuration.Version,
            text.ToString(),
            versionsAvailable ? null : manifest);
    }

    private static AssetEntry Require(AssetManifest manifest, Guid id, string assetType)
    {
        if (id == Guid.Empty || !manifest.Assets.TryGetValue(id, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {id} was not found in the runtime manifest.");
        if (!string.Equals(entry.AssetType, assetType, StringComparison.Ordinal))
            throw new InvalidDataException($"Asset {id} is {entry.AssetType}, not {assetType}.");
        return entry;
    }

}
