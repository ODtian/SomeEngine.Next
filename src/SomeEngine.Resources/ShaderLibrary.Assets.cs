using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Resources;

public sealed partial class ShaderLibrary
{
    /// <summary>Shares a completed shader by asset identity and its actual saved input/code bytes.</summary>
    public async ValueTask<Shader> GetAsync(AssetLoader files, AssetId<ShaderAsset> id,
        CancellationToken cancellationToken = default)
    {
        var reads = new ConcurrentDictionary<AssetId<ShaderAsset>, Lazy<Task<ShaderAssetInputs>>>();
        return (await GetAssetAsync(files, id, reads, cancellationToken).ConfigureAwait(false)).Shader;
    }

    private Task<ShaderAssetInputs> GetAssetAsync(AssetLoader files, AssetId<ShaderAsset> id,
        ConcurrentDictionary<AssetId<ShaderAsset>, Lazy<Task<ShaderAssetInputs>>> reads,
        CancellationToken cancellationToken) => reads.GetOrAdd(id,
            input => new Lazy<Task<ShaderAssetInputs>>(() => ReadAssetAsync(files, input, reads, cancellationToken))).Value;

    private async Task<ShaderAssetInputs> ReadAssetAsync(AssetLoader files, AssetId<ShaderAsset> id,
        ConcurrentDictionary<AssetId<ShaderAsset>, Lazy<Task<ShaderAssetInputs>>> reads,
        CancellationToken cancellationToken)
    {
        await using ShaderAsset asset = await ShaderAsset.OpenAsync(files, id, cancellationToken).ConfigureAwait(false);
        ShaderSourceBlock source = await asset.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        ShaderAssetInputs[] dependencies = await Task.WhenAll(source.Dependencies
            .Select(input => GetAssetAsync(files, input, reads, cancellationToken))).ConfigureAwait(false);
        ShaderInputSource[]? inputs = source.Inputs.Count != 0 || source.HasSerializedSources
            ? await asset.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false) : null;
        IEnumerable<ShaderInputVersion> ownVersions = inputs is null
            ? source.SourceNames.Select((_, index) => new ShaderInputVersion(
                new(ShaderSourceBlock.BlockTypeId, index), source.DataVersion, asset.Id))
            : inputs.Select(static input => input.Version);
        ShaderInputVersion[] versions = ShaderBlock.CollectInputVersions(ownVersions,
            dependencies.SelectMany(static input => input.InputVersions));
        ShaderKey key = await ReadKeyAsync(asset, inputs, versions, cancellationToken).ConfigureAwait(false);
        Task<ShaderAssetInputs> load = null!;
        ShaderAssetInputs? cached;
        TaskCompletionSource<ShaderAssetInputs>? creation = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _shaders.TryGetValue(key, out cached);
            if (cached is null && !_loads.TryGetValue(key, out load!))
            {
                creation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                load = creation.Task;
                _loads.Add(key, load);
            }
        }
        Shader[] dependencyShaders = dependencies.Select(static input => input.Shader).ToArray();
        if (creation is not null) _ = LoadAsync(key, asset.Duplicate(), creation, dependencyShaders, versions);
        if (cached is not null)
        {
            if (await RunAsync(() => ContainsInputs(cached.Shader), cancellationToken).ConfigureAwait(false)) return cached;
            lock (_gate)
            {
                if (!_loads.TryGetValue(key, out load!))
                {
                    creation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    load = creation.Task;
                    _loads.Add(key, load);
                }
            }
            if (creation is not null) _ = LoadAsync(key, asset.Duplicate(), creation, dependencyShaders, versions, cached);
        }
        return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadAsync(ShaderKey key, ShaderAsset asset,
        TaskCompletionSource<ShaderAssetInputs> completion, Shader[] dependencies,
        ShaderInputVersion[] versions, ShaderAssetInputs? cached = null)
    {
        try
        {
            Shader shader;
            await using (asset)
            {
                shader = await CreateAssetAsync(asset, default, default, dependencies, cached?.Shader, versions).ConfigureAwait(false);
                if (cached is null)
                    shader = await RunAsync(() => shader.WithDeclarationSources(dependencies)).ConfigureAwait(false);
            }
            var result = new ShaderAssetInputs(shader, versions);
            lock (_gate)
            {
                _shaders[key] = result;
                _loads.Remove(key);
            }
            completion.SetResult(result);
        }
        catch (Exception error)
        {
            lock (_gate) _loads.Remove(key);
            completion.SetException(error);
        }
    }

    private static async ValueTask<ShaderKey> ReadKeyAsync(ShaderAsset asset, ShaderInputSource[]? inputs,
        IReadOnlyList<ShaderInputVersion> versions, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShaderSourceBlock source = await asset.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        ShaderBlock code = await asset.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
        hash.AppendData(source.DataVersion.ToByteArray());
        foreach (ShaderInputVersion version in versions)
        {
            hash.AppendData(version.AssetId.ToByteArray());
            hash.AppendData(version.Input.BlockTypeId.ToByteArray());
            hash.AppendData(BitConverter.GetBytes(version.Input.SourceIndex));
            hash.AppendData(version.DataVersion.ToByteArray());
        }
        if (inputs is not null)
            foreach (ShaderInputSource input in inputs)
                hash.AppendData(input.Serialized.Span);
        foreach (ulong key in code.ContentKeys.Order())
        {
            hash.AppendData(BitConverter.GetBytes(key));
            hash.AppendData((await code.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false)).Span);
        }
        return new(asset.Id, BinaryPrimitives.ReadUInt64LittleEndian(hash.GetHashAndReset()));
    }

    internal async ValueTask<Shader> CreateAssetAsync(ShaderAsset asset,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken,
        IReadOnlyList<Shader>? dependencies = null, Shader? cached = null,
        IReadOnlyList<ShaderInputVersion>? dependencyVersions = null)
    {
        dependencies ??= [];
        await using ShaderAsset copy = asset.Duplicate();
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        if (source.Inputs.Count == 0 && !source.HasSerializedSources)
        {
            return await source.CreateAsync(this, copy.Name,
                specializations, cancellationToken, dependencies: dependencies).ConfigureAwait(false);
        }
        ShaderInputSource[] inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return await RunAsync(() =>
            {
                RestoreAssetInputs(inputs, dependencies ?? [], cached);
                return cached;
            }, cancellationToken).ConfigureAwait(false);
        ShaderBlock? savedCode = null;
        if (!source.SourceOnly && specializations.IsEmpty && copy.ContainsBlock<ShaderBlock>())
        {
            ShaderBlock code = await copy.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
            if (code.MatchesInputs(ShaderBlock.CollectInputVersions(inputs.Select(static input => input.Version),
                dependencyVersions ?? []), Settings, source.Specializations)) savedCode = code;
        }
        using ShaderSourceBlock selected = source.FromInputs(inputs);
        return savedCode is null
            ? await selected.CreateAsync(this, copy.Name, specializations, cancellationToken, inputs, dependencies).ConfigureAwait(false)
            : await savedCode.CreateAsync(selected, this, copy.Name,
                cancellationToken: cancellationToken, assetInputs: inputs, dependencies: dependencies).ConfigureAwait(false);
    }

    /// <summary>Creates a shader from an in-memory asset using this library's compiler.</summary>
    public async ValueTask<Shader> CreateAsync(ShaderAsset asset,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default)
    {
        await using ShaderAsset copy = asset.Duplicate();
        return await copy.CreateAsync(this, specializations, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Shader> CreateAsync(ShaderAsset asset, AssetLoader files,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default)
    {
        ShaderSourceBlock source = await asset.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        var reads = new ConcurrentDictionary<AssetId<ShaderAsset>, Lazy<Task<ShaderAssetInputs>>>();
        ShaderAssetInputs[] inputs = await Task.WhenAll(source.Dependencies
            .Select(id => GetAssetAsync(files, id, reads, cancellationToken))).ConfigureAwait(false);
        Shader[] dependencies = inputs.Select(static input => input.Shader).ToArray();
        Shader shader = await CreateAssetAsync(asset, specializations, cancellationToken, dependencies,
            dependencyVersions: inputs.SelectMany(static input => input.InputVersions).ToArray()).ConfigureAwait(false);
        return await RunAsync(() => shader.WithDeclarationSources(dependencies), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ShaderCompilation> CompileAsync(ShaderAsset asset, AssetLoader files,
        CancellationToken cancellationToken = default)
    {
        ShaderSourceBlock source = await asset.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        var reads = new ConcurrentDictionary<AssetId<ShaderAsset>, Lazy<Task<ShaderAssetInputs>>>();
        ShaderAssetInputs[] inputs = await Task.WhenAll(source.Dependencies
            .Select(id => GetAssetAsync(files, id, reads, cancellationToken))).ConfigureAwait(false);
        Shader[] dependencies = inputs.Select(static input => input.Shader).ToArray();
        ShaderCompilation compiled = await asset.CompileAsync(this, cancellationToken, dependencies,
            inputs.SelectMany(static input => input.InputVersions).ToArray()).ConfigureAwait(false);
        Shader shader = await RunAsync(() => compiled.Shader.WithDeclarationSources(dependencies), cancellationToken).ConfigureAwait(false);
        return compiled with { Shader = shader };
    }

    /// <summary>Creates entries from a programmatically supplied source block.</summary>
    public ValueTask<Shader> CreateAsync(ShaderSourceBlock source, string name,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default) =>
        source.CreateAsync(this, name, specializations, cancellationToken);

    public static async ValueTask<Shader> CreateAsync(ShaderAsset asset, IGlobalSession global,
        SessionDesc settings, ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(global, settings);
        return await library.CreateAsync(asset, specializations, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Shader> CreateAsync(ShaderSourceBlock source, IGlobalSession global,
        SessionDesc settings, string name, ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(global, settings);
        return await library.CreateAsync(source, name, specializations, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Shader> CreateAsync(ShaderBlock code, ShaderSourceBlock source,
        IGlobalSession global, SessionDesc settings, string name,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(global, settings);
        return await code.CreateAsync(source, library, name, specializations, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Shader> CreateAsync(ShaderBlock code, ShaderSourceBlock source,
        IGlobalSession global, SessionDesc settings, string name, string entryPoint,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(global, settings);
        return await code.CreateAsync(source, library, name, entryPoint, specializations, cancellationToken).ConfigureAwait(false);
    }
}
