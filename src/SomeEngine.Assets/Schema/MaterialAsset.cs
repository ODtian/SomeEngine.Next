using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>Material asset content with parent references and typed object creation.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Material")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public partial class MaterialAsset : Asset
{
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
    public MaterialAsset(Guid id) : base(id)
    {
        AssetType = FileType;
        using var content = new MaterialBlock();
        SetBlock(content);
    }
    protected MaterialAsset(MaterialAsset source) : base(source) { }
    [BinaryIgnore] public AssetId<MaterialAsset> Reference => new(Id);

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

    public static ValueTask<MaterialAsset> OpenAsync(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken = default) => OpenTypedAsync<MaterialAsset>(loader, id, cancellationToken);
    public static ValueTask<MaterialAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
        => OpenTypedAsync<MaterialAsset>(path, cancellationToken);

    protected static async ValueTask<TAsset> OpenTypedAsync<TAsset>(AssetLoader loader, AssetId<MaterialAsset> id,
        CancellationToken cancellationToken) where TAsset : MaterialAsset, new()
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync<TAsset>(file, id.Value, cancellationToken).ConfigureAwait(false);
    }
    protected static async ValueTask<TAsset> OpenTypedAsync<TAsset>(string path,
        CancellationToken cancellationToken) where TAsset : MaterialAsset, new()
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The material file has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync<TAsset>(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }
    private static async ValueTask<TAsset> ReadFileAsync<TAsset>(AssetFile file, Guid expected,
        CancellationToken cancellationToken) where TAsset : MaterialAsset, new()
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The material file has a different identity or type.");
        var result = new TAsset { Id = description.AssetGuid };
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            await result.GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gets independent complete content for the caller to acquire its shader and textures.</summary>
    public async ValueTask<MaterialBlock> ResolveAsync(AssetLoader files,
        CancellationToken cancellationToken = default)
    {
        using MaterialAsset copy = Duplicate();
        return await copy.ResolveContentAsync(files, [], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MaterialBlock> ResolveContentAsync(
        AssetLoader files, HashSet<Guid> ancestors, CancellationToken cancellationToken)
    {
        MaterialBlock content = await GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
        if (!ancestors.Add(Id)) throw new InvalidDataException("Material parent references contain a cycle.");
        MaterialBlock result;
        if (!content.Parent.IsEmpty)
        {
            using MaterialAsset parent = await OpenAsync(files, content.Parent, cancellationToken).ConfigureAwait(false);
            result = await parent.ResolveContentAsync(files, ancestors, cancellationToken).ConfigureAwait(false);
        }
        else result = new MaterialBlock();
        result.Name = Name;
        result.Parent = content.Parent;
        if (!content.Shader.IsEmpty) result.Shader = content.Shader;
        foreach (MaterialParameter value in content.Parameters) result.SetParameter(value);
        return result;
    }

    /// <summary>Gets the ancestor template identity used by the game's concrete material selection.</summary>
    public async ValueTask<AssetId<MaterialAsset>> GetTemplateAsync(AssetLoader loader,
        CancellationToken cancellationToken = default)
    {
        MaterialBlock content = await GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
        if (content.Parent.IsEmpty) return Reference;
        using MaterialAsset parent = await OpenAsync(loader, content.Parent, cancellationToken).ConfigureAwait(false);
        return await parent.GetTemplateAsync(loader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves complete content and creates the concrete material chosen by the caller.</summary>
    public async ValueTask<TMaterial> CreateAsync<TMaterial>(AssetLoader files,
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders,
        IReadOnlyDictionary<AssetId<TextureAsset>, Texture> textures,
        CancellationToken cancellationToken = default) where TMaterial : Material, IMaterial<TMaterial>
    {
        using MaterialBlock content = await ResolveAsync(files, cancellationToken).ConfigureAwait(false);
        Shader? shader = content.Shader.IsEmpty ? null : shaders[content.Shader];
        var values = new List<(MaterialValue Value, Texture? Texture)>(content.Parameters.Count);
        foreach (MaterialParameter parameter in content.Parameters)
        {
            Texture? texture = parameter.Kind == MaterialParameterKind.Texture && !parameter.Texture.IsEmpty
                ? textures[parameter.Texture] : null;
            values.Add((parameter.ToValue(), texture));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return TMaterial.Create(content.Name, shader, values);
    }

    /// <summary>Applies complete replacement fields from the caller's actual resource references.</summary>
    public async ValueTask UpdateAsync<TMaterial>(TMaterial material, AssetLoader files,
        IReadOnlyDictionary<AssetId<ShaderAsset>, Shader> shaders,
        IReadOnlyDictionary<AssetId<TextureAsset>, Texture> textures,
        CancellationToken cancellationToken = default) where TMaterial : Material, IMaterial<TMaterial>
    {
        TMaterial replacement = await CreateAsync<TMaterial>(files, shaders, textures, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        TMaterial.Update(material, replacement);
    }
}
