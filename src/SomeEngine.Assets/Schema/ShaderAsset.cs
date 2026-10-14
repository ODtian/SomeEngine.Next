using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>A shader identity containing independent source and compiled-code blocks.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Shader")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ShaderAsset : Asset
{
    public const uint Version = ShaderBlock.Version;
    public const string FileType = "SomeEngine.Assets.Schema.Shader";
    public const string PathSuffix = ".shader.asset";
    public static ulong SchemaHash => AssetHeader.SchemaFingerprint;
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    [BinaryIgnore] public AssetId<ShaderAsset> Reference => new(Id);
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }

    public ShaderAsset() : this(Guid.Empty) { }
    public ShaderAsset(Guid id = default) : base(id)
    {
        try
        {
            AssetType = FileType;
            using var source = new ShaderSourceBlock();
            using var code = new ShaderBlock();
            SetBlock(source);
            SetBlock(code);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    private ShaderAsset(ShaderAsset source) : base(source) { }
    public override ShaderAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override ShaderAsset CreateCopy(Guid id = default) => (ShaderAsset)base.CreateCopy(id);
    public IEnumerable<Guid> References() => BlockReferences();
    public AssetEntry Describe() => Describe(SchemaHash, References());

    /// <summary>Adds this block's Slang inputs to the shader's shared input order.</summary>
    public void SelectInputs<T>() where T : ShaderInputBlock, IAssetBlock<T>
    {
        lock (SyncRoot)
        {
            T block = GetBlock<T>();
            GetBlock<ShaderSourceBlock>().AddInputs(T.BlockTypeId, block.SourceNames.Count);
        }
    }

    public async ValueTask<Shader> CreateAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        using ShaderAsset copy = Duplicate();
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        if (source.Inputs.Count == 0)
        {
            if (copy.ContainsBlock<ShaderBlock>())
            {
                ShaderBlock code = await copy.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
                return await code.CreateAsync(source, slang, sessionDescription, copy.Name,
                    specializations, cancellationToken).ConfigureAwait(false);
            }
            return await source.CreateAsync(slang, sessionDescription, copy.Name,
                specializations, cancellationToken).ConfigureAwait(false);
        }
        return await copy.CreateFromInputsAsync(slang, sessionDescription, specializations,
            generateCode: true, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<Shader> CreateFromInputsAsync(
        IGlobalSession slang, SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations, bool generateCode,
        CancellationToken cancellationToken)
    {
        ShaderSourceBlock source = await GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        var selected = new ShaderInputSource[source.Inputs.Count];
        foreach (var group in source.Inputs.Select((input, index) => (Input: input, Index: index))
            .GroupBy(static input => input.Input.BlockTypeId))
        {
            if (GetBlock(group.Key) is { } block)
            {
                var inputBlock = (ShaderInputBlock)block;
                foreach (var input in group)
                {
                    int index = input.Input.SourceIndex;
                    selected[input.Index] = new(inputBlock.SourceNames[index],
                        await inputBlock.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index), cancellationToken).ConfigureAwait(false));
                }
            }
            else
            {
                using AssetBlockSource inputBlock = OpenBlockSource(group.Key);
                var data = new ShaderInputData();
                await inputBlock.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
                foreach (var input in group)
                {
                    int index = input.Input.SourceIndex;
                    selected[input.Index] = new(data.SourceNames[index],
                        await inputBlock.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index), cancellationToken).ConfigureAwait(false));
                }
            }
        }
        return await source.CreateSelectedAsync(selected, slang, sessionDescription, Name,
            specializations, generateCode, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using ShaderAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ShaderAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new ShaderAsset(description.AssetGuid);
        try
        {
            result.ReadBlockContent(file);
            result.AttachFile(file);
            if (result.ContainsBlock<ShaderSourceBlock>())
                await result.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
            if (result.ContainsBlock<ShaderBlock>())
                await result.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            try { result.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    public static async ValueTask<ShaderAsset> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The asset path has a different suffix.");
        await using AssetFile file = await AssetFile.OpenAsync(path, WireType, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, Guid.Empty, cancellationToken).ConfigureAwait(false);
    }
    public static async ValueTask<ShaderAsset> OpenAsync(AssetLoader loader, AssetId<ShaderAsset> id, CancellationToken cancellationToken = default)
    {
        await using AssetFile file = await loader.OpenFileAsync(id, FileType, WireType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, id.Value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AssetEntry> SaveSourceAsync(string path, IReadOnlyDictionary<ulong, string>? chunkFiles = null,
        CancellationToken cancellationToken = default)
    {
        using var writer = AssetText.Create(this);
        writer.WriteBlock<ShaderSourceBlock>(chunkFiles);
        return await writer.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ShaderAsset> OpenSourceAsync(string path, CancellationToken cancellationToken = default)
    {
        using AssetText.SourceReader source = await AssetText.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        source.RequireAsset<ShaderAsset>(FileType);
        var result = new ShaderAsset(source.Description.AssetGuid) { Name = source.Description.Name };
        try
        {
            await source.ReadBlockIntoAsync(result.GetBlock<ShaderSourceBlock>(), cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            try { result.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }

}
