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
        if (source.Inputs.Count == 0 && Enumerable.Range(0, source.SourceNames.Count)
            .Any(index => !source.ContentKeys.Contains(ShaderInputBlock.SerializedSourceKey(index))))
        {
            return await source.CreateAsync(slang, sessionDescription, copy.Name,
                specializations, cancellationToken).ConfigureAwait(false);
        }
        ShaderInputSource[] inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        ShaderBlock? savedCode = null;
        if (specializations.IsEmpty && copy.ContainsBlock<ShaderBlock>())
        {
            ShaderBlock code = await copy.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
            if (code.MatchesInputs(inputs.Select(static input => input.Version), sessionDescription)) savedCode = code;
        }
        Shader shader = await source.CreateSelectedAsync(inputs, slang, sessionDescription, copy.Name,
            specializations, generateCode: savedCode is null, cancellationToken).ConfigureAwait(false);
        return savedCode is null ? shader
            : await savedCode.ReadCodeAsync(shader, sessionDescription, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles the asset's complete inputs and saves their common target and entry code.</summary>
    public async ValueTask<ShaderCompilation> CompileAsync(
        IGlobalSession slang, SessionDesc sessionDescription, CancellationToken cancellationToken = default)
    {
        using ShaderAsset copy = Duplicate();
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        ShaderCompilation compiled;
        ShaderInputSource[] inputs;
        if (source.Inputs.Count == 0)
        {
            compiled = await source.CompileAsync(slang, sessionDescription, copy.Name, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < compiled.SerializedSources.Count; index++)
                source.SetSerializedSource(index, compiled.SerializedSources[index].Span);
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
            Shader shader = await source.CreateSelectedAsync(inputs, slang, sessionDescription, copy.Name,
                default, generateCode: true, cancellationToken).ConfigureAwait(false);
            compiled = new(shader, [], []);
        }
        using var code = new ShaderBlock();
        code.InputVersions = inputs.Select(static input => input.Version).ToList();
        for (int target = 0; target < compiled.Shader.SlangTargetCount; target++)
        {
            if (compiled.Shader.GetSlangTargetBlob(target) is { } targetCode)
                code.SetTargetCode(sessionDescription, target, targetCode.Buffer);
            for (int entry = 0; entry < compiled.Shader.SlangEntryCount; entry++)
                code.SetEntryCode(sessionDescription, target, entry, compiled.Shader.GetSlangEntryBlob(target, entry)!.Buffer);
        }
        lock (SyncRoot)
        {
            SetBlock(source);
            SetBlock(code);
        }
        return compiled;
    }

    private static ShaderInputLocation[] GetInputLocations(ShaderSourceBlock source)
        => source.Inputs.Count == 0
            ? Enumerable.Range(0, source.SourceNames.Count).Select(static index => new ShaderInputLocation(ShaderSourceBlock.BlockTypeId, index)).ToArray()
            : source.Inputs.ToArray();

    internal async ValueTask<Shader> CreateFromInputsAsync(
        IGlobalSession slang, SessionDesc sessionDescription,
        ReadOnlyMemory<SpecializationArg> specializations, bool generateCode,
        CancellationToken cancellationToken)
    {
        ShaderSourceBlock source = await GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        ShaderInputSource[] inputs = await ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        return await source.CreateSelectedAsync(inputs, slang, sessionDescription, Name,
            specializations, generateCode, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ShaderInputSource[]> ReadInputsAsync(ShaderSourceBlock source, CancellationToken cancellationToken)
    {
        ShaderInputLocation[] locations = GetInputLocations(source);
        var selected = new ShaderInputSource[locations.Length];
        foreach (var group in locations.Select((input, index) => (Input: input, Index: index))
            .GroupBy(static input => input.Input.BlockTypeId))
        {
            if (GetBlock(group.Key) is { } block)
            {
                var inputBlock = (ShaderInputBlock)block;
                foreach (var input in group)
                {
                    int index = input.Input.SourceIndex;
                    selected[input.Index] = new(inputBlock.SourceNames[index],
                        await inputBlock.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index), cancellationToken).ConfigureAwait(false),
                        new(input.Input, inputBlock.DataVersion));
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
                        await inputBlock.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index), cancellationToken).ConfigureAwait(false),
                        new(input.Input, data.DataVersion));
                }
            }
        }
        return selected;
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
