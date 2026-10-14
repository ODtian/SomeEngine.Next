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
    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
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

    private async ValueTask<(AssetId<ShaderAsset> Shader, Dictionary<string, MaterialParameter> Values)> ResolveAsync(
        AssetLoader loader, HashSet<Guid> ancestors, CancellationToken cancellationToken)
    {
        MaterialBlock content = await GetBlockAsync<MaterialBlock>(cancellationToken).ConfigureAwait(false);
        if (!ancestors.Add(Id)) throw new InvalidDataException("Material parent references contain a cycle.");
        (AssetId<ShaderAsset> Shader, Dictionary<string, MaterialParameter> Values) result =
            (content.Shader, new(StringComparer.Ordinal));
        if (!content.Parent.IsEmpty)
        {
            await using MaterialAsset parent = await OpenAsync(loader, content.Parent, cancellationToken).ConfigureAwait(false);
            result = await parent.ResolveAsync(loader, ancestors, cancellationToken).ConfigureAwait(false);
            if (!content.Shader.IsEmpty) result.Shader = content.Shader;
        }
        foreach (MaterialParameter value in content.Parameters) result.Values[value.Name] = value;
        return result;
    }

    /// <summary>Resolves complete content and creates the concrete material chosen by the caller.</summary>
    public async ValueTask<TMaterial> CreateAsync<TMaterial>(AssetLoader loader,
        CancellationToken cancellationToken = default) where TMaterial : Material, IMaterial<TMaterial>
    {
        using MaterialAsset copy = Duplicate();
        var content = await copy.ResolveAsync(loader, [], cancellationToken).ConfigureAwait(false);
        Shader? shader = content.Shader.IsEmpty ? null
            : await loader.GetShaderAsync(content.Shader, cancellationToken).ConfigureAwait(false);
        var values = new List<(MaterialValue Value, Texture? Texture)>(content.Values.Count);
        foreach (MaterialParameter parameter in content.Values.Values)
        {
            Texture? texture = parameter.Kind == MaterialParameterKind.Texture && !parameter.Texture.IsEmpty
                ? await loader.GetTextureAsync(parameter.Texture, cancellationToken).ConfigureAwait(false) : null;
            values.Add((parameter.ToValue(), texture));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return TMaterial.Create(copy.Name, shader, values);
    }

    /// <summary>Applies complete replacement fields at the caller's update boundary.</summary>
    public async ValueTask UpdateAsync<TMaterial>(TMaterial material, AssetLoader loader,
        CancellationToken cancellationToken = default) where TMaterial : Material, IMaterial<TMaterial>
    {
        using TMaterial replacement = await CreateAsync<TMaterial>(loader, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        TMaterial.Update(material, replacement);
    }
}
