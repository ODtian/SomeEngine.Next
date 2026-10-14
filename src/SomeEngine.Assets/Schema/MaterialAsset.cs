using SlangShaderSharp;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Assets.Schema;

/// <summary>A material identity with typed parameter content and explicit parent resolution.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Material")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public partial class MaterialAsset : Asset
{
    private static readonly object TypeGate = new();
    private static readonly Dictionary<Guid, (Func<Material> Value, Func<Guid, MaterialAsset> Asset)> Types = [];
    static MaterialAsset()
    {
        RegisterType<StandardMaterial, StandardMaterialAsset>(static id => new(id));
        RegisterType<UnlitMaterial, UnlitMaterialAsset>(static id => new(id));
    }
    public const string FileType = "SomeEngine.Assets.Schema.Material";
    public const string PathSuffix = ".material.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public MaterialAsset() : this(Guid.Empty) { }
    public MaterialAsset(Guid id = default) : this(id, Guid.Empty) { }
    protected MaterialAsset(Guid id, Guid materialType) : base(id)
    {
        try
        {
            AssetType = FileType;
            using var content = new MaterialBlock(materialType);
            SetBlock(content);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    protected MaterialAsset(MaterialAsset source) : base(source) { }
    [BinaryIgnore] public AssetId<MaterialAsset> Reference => new(Id);
    public static void RegisterType<TMaterial, TAsset>(Func<Guid, TAsset> createAsset)
        where TMaterial : Material, new() where TAsset : MaterialAsset
    {
        ArgumentNullException.ThrowIfNull(createAsset);
        using var value = new TMaterial();
        lock (TypeGate)
            if (!Types.TryAdd(value.MaterialTypeId, (static () => new TMaterial(), id => createAsset(id))))
                throw new InvalidOperationException($"Material type '{value.MaterialTypeId}' is already registered.");
    }

    public override MaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public override MaterialAsset CreateCopy(Guid id = default) => (MaterialAsset)base.CreateCopy(id);
    public AssetEntry Describe() => Describe(SchemaHash, References());
    public IEnumerable<Guid> References() => BlockReferences();
    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using MaterialAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<MaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<MaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The material file has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }
    protected static async ValueTask<TAsset> OpenTypedAsync<TAsset>(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken) where TAsset : MaterialAsset
    {
        MaterialAsset result = await OpenAsync(loader, id, cancellationToken).ConfigureAwait(false);
        if (result is TAsset typed) return typed;
        await result.DisposeAsync().ConfigureAwait(false);
        throw new InvalidDataException("The file contains a different concrete material type.");
    }
    protected static async ValueTask<TAsset> OpenTypedAsync<TAsset>(string path,
        CancellationToken cancellationToken) where TAsset : MaterialAsset
    {
        MaterialAsset result = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (result is TAsset typed) return typed;
        await result.DisposeAsync().ConfigureAwait(false);
        throw new InvalidDataException("The file contains a different concrete material type.");
    }
    private static async ValueTask<MaterialAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The material file has a different identity or type.");
        var root = new MaterialAsset(description.AssetGuid);
        try
        {
            root.ReadBlockContent(file);
            root.AttachFile(file);
            MaterialBlock content = await root.GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
            if (content.MaterialTypeId == Guid.Empty) return root;
            MaterialAsset result;
            lock (TypeGate)
                result = Types.TryGetValue(content.MaterialTypeId, out var factories)
                    ? factories.Asset(description.AssetGuid)
                    : throw new InvalidDataException($"Unknown material type '{content.MaterialTypeId}'.");
            try
            {
                if (result.GetBlock<MaterialBlock>().MaterialTypeId != content.MaterialTypeId)
                    throw new InvalidDataException("The material factory returned a different concrete material type.");
                result.Name = root.Name;
                result.BlockDescriptions = root.BlockDescriptions;
                result.SetBlock(content);
                result.AttachFile(file);
                root.Dispose();
                return result;
            }
            catch (Exception error)
            {
                try { result.Dispose(); }
                catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
                throw;
            }
        }
        catch (Exception error)
        {
            try { root.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    private async ValueTask<(Guid Type, AssetId<ShaderAsset> Shader, Dictionary<string, MaterialParameter> Values)> ResolveAsync(
        AssetLoader loader, HashSet<Guid> ancestors, CancellationToken cancellationToken)
    {
        using MaterialAsset copy = Duplicate();
        MaterialBlock content = await copy.GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
        if (!ancestors.Add(copy.Id)) throw new InvalidDataException("Material parent references contain a cycle.");
        (Guid Type, AssetId<ShaderAsset> Shader, Dictionary<string, MaterialParameter> Values) result =
            (content.MaterialTypeId, content.Shader, new(StringComparer.Ordinal));
        if (!content.Parent.IsEmpty)
        {
            await using MaterialAsset parent = await OpenAsync(loader, content.Parent, cancellationToken).ConfigureAwait(false);
            result = await parent.ResolveAsync(loader, ancestors, cancellationToken).ConfigureAwait(false);
            if (content.MaterialTypeId != Guid.Empty && content.MaterialTypeId != result.Type)
                throw new InvalidDataException("A material override cannot change the parent's material type.");
            if (!content.Shader.IsEmpty) result.Shader = content.Shader;
        }
        foreach (MaterialParameter value in content.Parameters) result.Values[value.Name] = value;
        return result;
    }
    public ValueTask<Material> CreateAsync(AssetLoader loader, CancellationToken cancellationToken = default)
        => CreateAsync(loader, null, null, cancellationToken);

    public ValueTask<Material> CreateAsync(
        AssetLoader loader,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        return CreateCoreAsync(
            loader,
            LoadShader,
            null,
            cancellationToken,
            loadTexturesConcurrently: false);

        async ValueTask<Shader> LoadShader(
            AssetId<ShaderAsset> id,
            CancellationToken loadCancellationToken)
        {
            Dictionary<Guid, Shader> shaders = await ShaderRef.LoadAsync(
                [id.Value],
                loader,
                slang,
                sessionDescription,
                specializations,
                loadCancellationToken).ConfigureAwait(false);
            return shaders[id.Value];
        }
    }

    /// <summary>Creates material fields using direct references returned by the caller.
    /// Supplied shader and texture objects remain the caller's responsibility; disposing the material
    /// does not dispose them. Omitted loaders create private dependencies as in the simple overload.
    /// Distinct texture loader calls may overlap when loadTexturesConcurrently is true.</summary>
    public ValueTask<Material> CreateAsync(AssetLoader loader,
        Func<AssetId<ShaderAsset>, CancellationToken, ValueTask<Shader>>? loadShader,
        Func<AssetId<TextureAsset>, CancellationToken, ValueTask<Texture>>? loadTexture,
        CancellationToken cancellationToken = default,
        bool loadTexturesConcurrently = false) =>
        CreateCoreAsync(
            loader,
            loadShader,
            loadTexture,
            cancellationToken,
            loadTexturesConcurrently);

    private async ValueTask<Material> CreateCoreAsync(
        AssetLoader loader,
        Func<AssetId<ShaderAsset>, CancellationToken, ValueTask<Shader>>? loadShader,
        Func<AssetId<TextureAsset>, CancellationToken, ValueTask<Texture>>? loadTexture,
        CancellationToken cancellationToken,
        bool loadTexturesConcurrently)
    {
        ArgumentNullException.ThrowIfNull(loader);
        using MaterialAsset copy = Duplicate();
        var resolved = await copy.ResolveAsync(loader, [], cancellationToken).ConfigureAwait(false);
        Material result;
        lock (TypeGate)
            result = Types.TryGetValue(resolved.Type, out var factories) ? factories.Value()
                : throw new InvalidDataException($"Unknown material type '{resolved.Type}'.");
        Shader? shader = null;
        var textures = new Dictionary<Guid, Texture>();
        var values = new List<(MaterialValue, Texture?)>();
        bool transferred = false;
        try
        {
            if (!resolved.Shader.IsEmpty)
            {
                if (loadShader is not null)
                    shader = await loadShader(resolved.Shader, cancellationToken).ConfigureAwait(false);
                else
                {
                    throw new InvalidOperationException(
                        "Creating a material with a shader requires an explicit shader-loading behavior.");
                }
            }
            var textureReads = new Dictionary<Guid, Task<Texture>>();
            if (loadTexture is not null && loadTexturesConcurrently)
            {
                foreach (MaterialParameter value in resolved.Values.Values)
                    if (value.Kind == MaterialParameterKind.Texture && !value.Texture.IsEmpty &&
                        !textureReads.ContainsKey(value.Texture.Value))
                    {
                        textureReads.Add(value.Texture.Value,
                            loadTexture(value.Texture, cancellationToken).AsTask());
                    }
                await Task.WhenAll(textureReads.Values).ConfigureAwait(false);
            }
            foreach (MaterialParameter value in resolved.Values.Values)
            {
                Texture? texture = null;
                if (value.Kind == MaterialParameterKind.Texture && !value.Texture.IsEmpty)
                {
                    if (!textures.TryGetValue(value.Texture.Value, out texture))
                    {
                        if (loadTexture is not null)
                            texture = loadTexturesConcurrently
                                ? textureReads[value.Texture.Value].Result
                                : await loadTexture(value.Texture, cancellationToken).ConfigureAwait(false);
                        else
                        {
                            await using TextureAsset file = await TextureAsset.OpenAsync(loader, value.Texture, cancellationToken).ConfigureAwait(false);
                            TextureBlock pixels = await file.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
                            texture = await pixels.CreateAsync(file.Name, cancellationToken).ConfigureAwait(false);
                        }
                        textures.Add(value.Texture.Value, texture);
                    }
                }
                values.Add((value.ToValue(), texture));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Texture[] createdTextures = loadTexture is null ? [.. textures.Values] : [];
            transferred = true;
            result.InitializeContent(copy.Name, shader, values, createdTextures);
            return result;
        }
        catch
        {
            try { result.Dispose(); }
            finally
            {
                if (!transferred)
                {
                    if (loadTexture is null)
                        foreach (Texture texture in textures.Values) texture.Dispose();
                }
            }
            throw;
        }
    }
    /// <summary>Loads and applies replacement content while the caller holds its resource update boundary.</summary>
    /// <remarks>Keep CPU rendering paused until this operation finishes. To overlap file loading
    /// with rendering, use CreateAsync first and call Material.Update at the next update boundary.</remarks>
    public async ValueTask UpdateAsync(Material material, AssetLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        ObjectDisposedException.ThrowIf(material.IsDisposed, material);
        using Material replacement = await CreateAsync(loader, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        material.Update(replacement);
    }

    public async ValueTask UpdateAsync(Material material, AssetLoader loader,
        IGlobalSession slang, SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        ObjectDisposedException.ThrowIf(material.IsDisposed, material);
        using Material replacement = await CreateAsync(loader, slang, sessionDescription,
            specializations, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        material.Update(replacement);
    }

    public async ValueTask UpdateAsync(Material material, AssetLoader loader,
        Func<AssetId<ShaderAsset>, CancellationToken, ValueTask<Shader>>? loadShader,
        Func<AssetId<TextureAsset>, CancellationToken, ValueTask<Texture>>? loadTexture,
        CancellationToken cancellationToken = default, bool loadTexturesConcurrently = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        ObjectDisposedException.ThrowIf(material.IsDisposed, material);
        using Material replacement = await CreateAsync(loader, loadShader, loadTexture,
            cancellationToken, loadTexturesConcurrently).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        material.Update(replacement);
    }
}
