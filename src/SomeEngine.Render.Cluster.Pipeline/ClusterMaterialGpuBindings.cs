using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization.Streaming;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using AssetTexture = SomeEngine.Assets.Schema.Texture;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the GPU-resolved material bindings for one immutable Cluster material topology.
/// </summary>
internal sealed class ClusterMaterialGpuBindings : IDisposable
{
    private readonly Device _device;
    private readonly IGraphicsBackend _backend;
    private readonly AssetLoader _assets;
    private readonly Dictionary<AssetGuid, Texture> _textures = [];
    private ClusterMaterialGpuBinding[] _bindings = [];
    private ulong _topologyVersion;
    private Sampler? _materialSampler;
    private Texture? _cookieAtlas;
    private bool _disposed;

    internal ClusterMaterialGpuBindings(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Sampler? materialSampler = null;
        Texture? cookieAtlas = null;
        try
        {
            materialSampler = backend.CreateSampler(device, new SamplerDesc(
                FilterType.Linear,
                FilterType.Linear,
                FilterType.Linear,
                AddressType.Repeat,
                AddressType.Repeat,
                AddressType.ClampToEdge,
                Label: "Cluster material sampler"));
            cookieAtlas = CreateSolidTexture(
                [255, 255, 255, 255],
                "Cluster empty cookie atlas");
            _materialSampler = materialSampler;
            _cookieAtlas = cookieAtlas;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (cookieAtlas is not null)
                TryDispose(cookieAtlas, ref cleanupFailures);
            if (materialSampler is not null)
                TryDispose(materialSampler, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster material GPU binding construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    internal ReadOnlySpan<ClusterMaterialGpuBinding> Bindings
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bindings;
        }
    }

    internal Sampler MaterialSampler => _materialSampler ??
        throw new ObjectDisposedException(nameof(ClusterMaterialGpuBindings));

    internal Texture CookieAtlas => _cookieAtlas ??
        throw new ObjectDisposedException(nameof(ClusterMaterialGpuBindings));

    internal void EnsureBindings(ClusterMaterialSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MaterialCount == 0 || snapshot.TopologyVersion == 0)
        {
            throw new ArgumentException(
                "Cluster GPU bindings require a published, non-empty material topology.",
                nameof(snapshot));
        }
        if (_topologyVersion == snapshot.TopologyVersion)
            return;
        if (_topologyVersion != 0)
        {
            // RenderWorld prepare publishes immutable tables. A material topology change while an
            // older GPU frame may still exist requires a new renderer epoch rather than silently
            // destroying its pipelines and textures.
            throw new InvalidOperationException(
                "Cluster material topology changed after the renderer epoch was initialized.");
        }

        var bindings = new ClusterMaterialGpuBinding[snapshot.Materials.Count];
        var createdTextures = new List<AssetGuid>(snapshot.Materials.Count);
        try
        {
            for (int index = 0; index < bindings.Length; index++)
            {
                bindings[index] = CreateBinding(
                    snapshot.Materials[index],
                    checked((uint)index),
                    createdTextures);
            }
            _bindings = bindings;
            _topologyVersion = snapshot.TopologyVersion;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            for (int index = bindings.Length - 1; index >= 0; index--)
            {
                if (bindings[index] is { } binding)
                    TryDispose(binding, ref cleanupFailures);
            }
            for (int index = createdTextures.Count - 1; index >= 0; index--)
            {
                if (_textures.Remove(createdTextures[index], out Texture? texture))
                    TryDispose(texture, ref cleanupFailures);
            }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster material binding publication failed and rollback also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private ClusterMaterialGpuBinding CreateBinding(
        AssetHandle<Material> handle,
        uint bin,
        List<AssetGuid> createdTextures)
    {
        if (!handle.IsValid || handle.LoadState != AssetLoadState.Ready)
            throw new InvalidOperationException($"Cluster material bin {bin} is not ready.");
        ClusterComputePipeline? shadePipeline = null;
        Buffer? scalarBuffer = null;
        try
        {
            string materialName;
            ClusterMaterialParameterKind parameterKind;
            byte[] scalarBytes;
            Dictionary<string, AssetGuid> textureGuids;
            Dictionary<string, Texture> textures;
            using (AssetRead<Material> materialRead = _assets.Read(handle))
            {
                Material material = materialRead.Value;
                AssetGuid selectedShaderGuid = default;
                string? selectedEntry = null;
                bool selectedCached = false;
                foreach (PassEntry pass in material.Passes ?? [])
                {
                    if (!AssetGuid.TryParse(pass.ShaderGuid, out AssetGuid shaderGuid) || shaderGuid.IsEmpty)
                        continue;
                    using AssetRead<Shader> shaderRead = LoadShaderRead(shaderGuid);
                    string? candidate = ResolveShadeEntry(shaderRead.Value, pass.EntryPoint);
                    if (candidate is null)
                        continue;
                    bool candidateCached = IsTarget(
                        shaderRead.Value,
                        candidate,
                        "cluster.shade.cached");
                    if (selectedEntry is null || candidateCached && !selectedCached)
                    {
                        selectedShaderGuid = shaderGuid;
                        selectedEntry = candidate;
                        selectedCached = candidateCached;
                    }
                }
                if (selectedShaderGuid.IsEmpty || string.IsNullOrWhiteSpace(selectedEntry))
                {
                    throw new InvalidDataException(
                        $"Material '{material.Name}' has no Cluster shade entry point.");
                }

                materialName = material.Name ?? $"material-{bin}";
                using AssetRead<Shader> selectedShaderRead = LoadShaderRead(selectedShaderGuid);
                Shader selectedShader = selectedShaderRead.Value;
                parameterKind = ResolveParameterKind(selectedShader);
                scalarBytes = CreateScalarRegion(selectedShader, material);
                textureGuids = new Dictionary<string, AssetGuid>(
                    material.Textures?.Count ?? 0,
                    StringComparer.Ordinal);
                foreach (TextureBinding binding in material.Textures ?? [])
                {
                    if (string.IsNullOrWhiteSpace(binding.Name) ||
                        !AssetGuid.TryParse(binding.TextureGuid, out AssetGuid textureGuid) ||
                        textureGuid.IsEmpty)
                    {
                        throw new InvalidDataException(
                            $"Material '{material.Name}' has an invalid texture binding.");
                    }
                    textureGuids.Add(binding.Name, textureGuid);
                }

                textures = new Dictionary<string, Texture>(
                    textureGuids.Count,
                    StringComparer.Ordinal);
                _textures.EnsureCapacity(checked(_textures.Count + textureGuids.Count));
                createdTextures.EnsureCapacity(checked(createdTextures.Count + textureGuids.Count));
                shadePipeline = ClusterComputePipeline.Create(
                    _backend,
                    _device,
                    selectedShader,
                    selectedEntry,
                    $"Cluster material shade {materialName}");
            }

            foreach ((string name, AssetGuid textureGuid) in textureGuids)
                textures.Add(name, GetOrUploadTexture(textureGuid, createdTextures));

            scalarBuffer = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)Math.Max(ScalarLayout.HeaderByteSize, scalarBytes.Length)),
                    BufferUsages.ShaderRead,
                    $"Cluster material scalars {materialName}"),
                MemoryType.Upload);
            BufferRange scalarRange = new(0, checked((ulong)scalarBytes.Length));
            using (MappedBuffer mapping = _backend.Map(scalarBuffer, MapType.Write, scalarRange))
            {
                scalarBytes.CopyTo(mapping.Bytes);
                mapping.Flush(scalarRange);
            }

            var result = new ClusterMaterialGpuBinding(
                bin,
                materialName,
                shadePipeline,
                parameterKind,
                scalarBuffer,
                textures,
                MaterialSampler);
            shadePipeline = null;
            scalarBuffer = null;
            return result;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (scalarBuffer is not null)
                TryDispose(scalarBuffer, ref cleanupFailures);
            if (shadePipeline is not null)
                TryDispose(shadePipeline, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Cluster material bin {bin} construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private AssetRead<Shader> LoadShaderRead(AssetGuid guid)
    {
        AssetHandle<Shader> handle = _assets.Load(new AssetId<Shader>(guid));
        if (handle.LoadState != AssetLoadState.Ready)
            _assets.WaitAsync(handle).AsTask().GetAwaiter().GetResult();
        return _assets.Read(handle);
    }

    private static ClusterMaterialParameterKind ResolveParameterKind(Shader shader)
    {
        HashSet<string> bindings = (shader.Metadata?.MaterialBindings ?? [])
            .Select(static binding => binding.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.Ordinal);
        if (bindings.Contains("AlbedoMap") &&
            bindings.Contains("NormalMap") &&
            bindings.Contains("ARMMap"))
            return ClusterMaterialParameterKind.StandardPbr;
        if (bindings.SetEquals(["AlbedoMap"]))
            return ClusterMaterialParameterKind.Unlit;
        throw new NotSupportedException(
            "Cluster material shader has no source-generated pass parameter shape.");
    }

    private static string? ResolveShadeEntry(Shader shader, string? authored)
    {
        if (!string.IsNullOrWhiteSpace(authored) &&
            shader.TryVariant("dxil", authored, AssetShaderStage.Compute, out _))
        {
            return IsTarget(shader, authored, "cluster.shade") ||
                IsTarget(shader, authored, "cluster.shade.cached")
                    ? authored
                    : null;
        }

        string? uncached = null;
        foreach (ShaderEntryPointAttribute attribute in shader.EntryPointAttributes ?? [])
        {
            if (!string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal) ||
                attribute.Args is not { Count: > 0 } args ||
                !shader.TryEntry(attribute, preferred: null, out string entry) ||
                !shader.TryVariant("dxil", entry, AssetShaderStage.Compute, out _))
            {
                continue;
            }
            if (string.Equals(args[0], "cluster.shade.cached", StringComparison.Ordinal))
                return entry;
            if (string.Equals(args[0], "cluster.shade", StringComparison.Ordinal))
                uncached = entry;
        }
        return uncached;
    }

    private static bool IsTarget(Shader shader, string entry, string target)
    {
        foreach (ShaderEntryPointAttribute attribute in shader.EntryPointAttributes ?? [])
        {
            if (!string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal) ||
                attribute.Args is not { Count: > 0 } args ||
                !string.Equals(args[0], target, StringComparison.Ordinal) ||
                !shader.TryEntry(attribute, entry, out string candidate))
            {
                continue;
            }
            if (string.Equals(candidate, entry, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static byte[] CreateScalarRegion(Shader shader, Material material)
    {
        ShaderMaterialScalarLayout data = (shader.Metadata?.MaterialScalarLayouts ?? [])
            .SingleOrDefault()
            ?? throw new InvalidDataException(
                $"Material shader '{shader.Name}' has no unique scalar layout.");
        ScalarLayout layout = ScalarLayout.FromData(data);
        byte[] bytes = new byte[layout.ByteSize];
        layout.WriteHeader(bytes);
        Span<byte> payload = bytes.AsSpan(ScalarLayout.HeaderByteSize);
        foreach (ScalarParam parameter in material.Scalars ?? [])
        {
            if (string.IsNullOrWhiteSpace(parameter.Name) || parameter.Value is not { } value)
                continue;
            switch (value.Kind)
            {
                case ParamValue.ItemKind.FloatVal:
                    layout.Write(parameter.Name, value.FloatVal.V, payload);
                    break;
                case ParamValue.ItemKind.IntVal:
                    layout.Write(parameter.Name, value.IntVal.V, payload);
                    break;
                case ParamValue.ItemKind.BoolVal:
                    layout.Write(parameter.Name, value.BoolVal.V ? 1 : 0, payload);
                    break;
                case ParamValue.ItemKind.Vec2Val:
                    layout.Write(parameter.Name, new Vector4(value.Vec2Val.X, value.Vec2Val.Y, 0, 0), payload);
                    break;
                case ParamValue.ItemKind.Vec3Val:
                    layout.Write(parameter.Name, new Vector3(value.Vec3Val.X, value.Vec3Val.Y, value.Vec3Val.Z), payload);
                    break;
                case ParamValue.ItemKind.Vec4Val:
                    layout.Write(parameter.Name, new Vector4(
                        value.Vec4Val.X,
                        value.Vec4Val.Y,
                        value.Vec4Val.Z,
                        value.Vec4Val.W), payload);
                    break;
            }
        }
        return bytes;
    }

    private Texture GetOrUploadTexture(AssetGuid guid, List<AssetGuid> createdTextures)
    {
        if (_textures.TryGetValue(guid, out Texture? existing))
            return existing;
        AssetHandle<AssetTexture> handle = _assets.Load(new AssetId<AssetTexture>(guid));
        if (handle.LoadState != AssetLoadState.Ready)
            _assets.WaitAsync(handle).AsTask().GetAwaiter().GetResult();
        _textures.EnsureCapacity(checked(_textures.Count + 1));
        createdTextures.EnsureCapacity(checked(createdTextures.Count + 1));
        Texture? created = null;
        bool published = false;
        try
        {
            using (AssetRead<AssetTexture> read = _assets.Read(handle))
                created = UploadTexture(read.Value, guid.ToFlatString());
            _textures.Add(guid, created);
            published = true;
            createdTextures.Add(guid);
            Texture result = created;
            created = null;
            return result;
        }
        catch (Exception primary)
        {
            if (published)
                _textures.Remove(guid);
            List<Exception>? cleanupFailures = null;
            if (created is not null)
                TryDispose(created, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Cluster material texture '{guid.ToFlatString()}' failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private Texture UploadTexture(AssetTexture source, string name)
    {
        TextureMipTile tile = (source.MipTiles ?? [])
            .Single(item => item.MipLevel == 0 && item.ArrayLayer == 0 && item.TileX == 0 && item.TileY == 0);
        if (tile.Width != source.Width || tile.Height != source.Height)
            throw new NotSupportedException("Cluster material texture upload currently requires one complete mip-zero tile.");
        Format format = ParseFormat(source.Format);
        TextureDesc description = new(
            TextureDimension.Texture2D,
            source.Width,
            source.Height,
            1,
            1,
            1,
            1,
            format,
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            label: $"Cluster material texture {name}");
        Texture texture = _backend.CreateTexture(_device, description);
        try
        {
            using ResidentChunkLease lease = source.AcquireMipTileAsync(0, 0, 0)
                .AsTask().GetAwaiter().GetResult();
            UploadTexture(description, texture, lease.Memory.Span, checked((uint)tile.RowPitch));
            return texture;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            TryDispose(texture, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Cluster material texture '{name}' upload failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private Texture CreateSolidTexture(ReadOnlySpan<byte> rgba, string name)
    {
        TextureDesc description = new(
            TextureDimension.Texture2D,
            1,
            1,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            label: name);
        Texture texture = _backend.CreateTexture(_device, description);
        try
        {
            UploadTexture(description, texture, rgba, 4);
            return texture;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            TryDispose(texture, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Cluster solid texture '{name}' upload failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private void UploadTexture(
        in TextureDesc description,
        Texture texture,
        ReadOnlySpan<byte> source,
        uint sourceRowPitch)
    {
        BufferTextureCopy footprintRequest = new(
            null!,
            0,
            0,
            0,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            description.Width,
            description.Height,
            1);
        TextureCopyFootprint footprint = _backend.GetTextureCopyFootprint(
            _device,
            description,
            footprintRequest);
        byte[] uploadBytes = new byte[checked((int)footprint.TotalSize)];
        for (uint row = 0; row < description.Height; row++)
        {
            source.Slice(checked((int)(row * sourceRowPitch)), checked((int)footprint.RowSize))
                .CopyTo(uploadBytes.AsSpan(
                    checked((int)(footprint.Offset + (ulong)row * footprint.RowPitch)),
                    checked((int)footprint.RowSize)));
        }
        Buffer upload = _backend.CreateBuffer(
            _device,
            new BufferDesc(footprint.TotalSize, BufferUsages.CopySource, "Cluster texture upload"),
            MemoryType.Upload);
        try
        {
            BufferRange uploadRange = new(0, checked((ulong)uploadBytes.Length));
            using (MappedBuffer mapping = _backend.Map(upload, MapType.Write, uploadRange))
            {
                uploadBytes.CopyTo(mapping.Bytes);
                mapping.Flush(uploadRange);
            }

            using CommandContext commands = _backend.CreateCommandContext(
                _device,
                new CommandContextDesc(QueueType.Graphics, 0, 1, Label: "Cluster texture upload"));
            _backend.Begin(commands);
            bool recording = true;
            RecordedCommands recorded;
            try
            {
                TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
                _backend.Barrier(commands, new TextureBarrier(
                    texture,
                    textureRange,
                    texture.InitialSync,
                    PipelineSync.Copy,
                    texture.InitialAccess,
                    ResourceAccess.CopyDestination,
                    texture.InitialLayout,
                    TextureLayout.CopyDestination));
                _backend.Barrier(commands, new BufferBarrier(
                    upload,
                    upload.InitialSync,
                    PipelineSync.Copy,
                    upload.InitialAccess,
                    ResourceAccess.CopySource));
                BufferTextureCopy copy = footprintRequest with
                {
                    Buffer = upload,
                    BufferOffset = footprint.Offset,
                    BufferRowPitch = footprint.RowPitch,
                    BufferImageHeight = footprint.RowCount,
                };
                _backend.CopyBufferToTexture(commands, copy);
                _backend.Barrier(commands, new TextureBarrier(
                    texture,
                    textureRange,
                    PipelineSync.Copy,
                    PipelineSync.AllShading,
                    ResourceAccess.CopyDestination,
                    ResourceAccess.ShaderResource,
                    TextureLayout.CopyDestination,
                    TextureLayout.ShaderResource));
                recorded = _backend.End(commands);
                recording = false;
            }
            catch
            {
                if (recording)
                    _backend.Discard(commands);
                throw;
            }

            using (recorded)
            {
                RecordedCommands[] payload = [recorded];
                QueueSubmitDesc submission = new(default, default, payload, default, default);
                Queue queue = _backend.GetQueue(_device, QueueType.Graphics);
                QueueCompletion completion = _backend.Submit(queue, submission);
                if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
                    throw new TimeoutException("Cluster material texture upload did not complete.");
            }
        }
        finally
        {
            upload.Dispose();
        }
    }

    private static Format ParseFormat(string? value) => value switch
    {
        "RGBA8_UNorm" => Format.R8G8B8A8UNorm,
        "RGBA8_UNorm_SRGB" => Format.R8G8B8A8UNormSrgb,
        _ => throw new NotSupportedException($"Cluster material texture format '{value}' is not supported."),
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (int index = _bindings.Length - 1; index >= 0; index--)
            TryDispose(_bindings[index], ref failures);
        foreach (Texture texture in _textures.Values)
            TryDispose(texture, ref failures);
        if (_cookieAtlas is { } cookieAtlas)
            Try(cookieAtlas.Dispose, ref failures);
        if (_materialSampler is { } materialSampler)
            Try(materialSampler.Dispose, ref failures);
        _bindings = [];
        _textures.Clear();
        _cookieAtlas = null;
        _materialSampler = null;
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
        => Try(value.Dispose, ref failures);

    private static void Try(Action action, ref List<Exception>? failures)
    {
        try { action(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }

}

/// <summary>
/// One resolved material-bin binding. It owns its shade pipeline and scalar buffer, while texture
/// and sampler references are borrowed from the containing <see cref="ClusterMaterialGpuBindings"/>.
/// </summary>
internal sealed class ClusterMaterialGpuBinding : IDisposable
{
    private bool _disposed;

    internal ClusterMaterialGpuBinding(
        uint bin,
        string name,
        ClusterComputePipeline shadePipeline,
        ClusterMaterialParameterKind parameterKind,
        Buffer scalars,
        IReadOnlyDictionary<string, Texture> textures,
        Sampler sampler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shadePipeline);
        ArgumentNullException.ThrowIfNull(scalars);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(sampler);
        Bin = bin;
        Name = name;
        ShadePipeline = shadePipeline;
        ParameterKind = parameterKind;
        ScalarBuffer = scalars;
        Textures = textures;
        Sampler = sampler;
    }

    internal uint Bin { get; }
    internal string Name { get; }
    internal ClusterComputePipeline ShadePipeline { get; }
    internal ClusterMaterialParameterKind ParameterKind { get; }
    internal Buffer ScalarBuffer { get; }
    internal IReadOnlyDictionary<string, Texture> Textures { get; }
    internal Sampler Sampler { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        try { ShadePipeline.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { ScalarBuffer.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal enum ClusterMaterialParameterKind : byte
{
    StandardPbr,
    Unlit,
}
