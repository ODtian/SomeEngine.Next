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

    internal ValueTask<Shader> CreateAsync(ShaderLibrary library,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default) =>
        library.CreateAssetAsync(this, specializations, cancellationToken);

    /// <summary>Compiles the asset's complete inputs and saves their common target and entry code.</summary>
    public async ValueTask<ShaderCompilation> CompileAsync(
        IGlobalSession slang, SessionDesc sessionDescription, CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(slang, sessionDescription);
        return await CompileAsync(library, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ShaderCompilation> CompileAsync(ShaderLibrary library, CancellationToken cancellationToken,
        IReadOnlyList<Shader>? dependencies = null, IReadOnlyList<ShaderInputVersion>? dependencyVersions = null)
    {
        dependencies ??= [];
        SessionDesc sessionDescription = library.Settings;
        using ShaderAsset copy = Duplicate();
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        ShaderCompilation compiled;
        ShaderInputSource[] inputs;
        if (source.Inputs.Count == 0 && !source.HasSerializedSources)
        {
            compiled = await library.CompileAsync(source, copy.Name, default, generateCode: true, saveSources: true,
                cancellationToken, dependencies: dependencies).ConfigureAwait(false);
            for (int index = 0; index < compiled.SerializedSources.Count; index++)
                source.SetSerializedSource(index, compiled.SerializedSources[index].Span);
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
            using ShaderSourceBlock combined = source.FromInputs(inputs);
            compiled = await library.CompileAsync(combined, copy.Name, default,
                generateCode: true, saveSources: false, cancellationToken,
                assetInputs: inputs, dependencies: dependencies).ConfigureAwait(false);
        }
        using var code = new ShaderBlock();
        code.InputVersions = ShaderBlock.CollectInputVersions(inputs.Select(static input => input.Version), dependencyVersions ?? []);
        SaveCode(compiled.Shader, [], null);
        using ShaderSourceBlock selected = source.FromInputs(inputs);
        source.Specializations = compiled.Specializations.ToList();
        inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        code.InputVersions = ShaderBlock.CollectInputVersions(inputs.Select(static input => input.Version), dependencyVersions ?? []);
        foreach (ShaderSpecialization variant in compiled.Specializations)
        {
            // Code generation belongs to asset preparation, alongside the common entries.
            ShaderCompilation cooked = await library.CompileAsync(selected, copy.Name, variant.ToSlangArguments(),
                generateCode: true, saveSources: false, cancellationToken, variant.EntryPoint,
                inputs, dependencies, compiled.Shader).ConfigureAwait(false);
            SaveCode(cooked.Shader, variant.Arguments.ToArray(), variant.EntryPoint);
        }

        void SaveCode(Shader shader, string[] arguments, string? entryPoint)
        {
            for (int target = 0; target < shader.SlangTargetCount; target++)
            {
                if (shader.GetSlangTargetBlob(target) is { } targetCode)
                    code.SetTargetCode(sessionDescription, target, targetCode.Buffer, arguments, entryPoint);
                for (int entry = 0; entry < shader.SlangEntryCount; entry++)
                {
                    if (shader.GetSlangEntryPoint(entry).GetSpecializationParamCount() != 0 && entryPoint is null) continue;
                    if (shader.GetSlangEntryBlob(target, entry) is { } entryCode)
                        code.SetEntryCode(sessionDescription, target, entry, entryCode.Buffer, arguments, entryPoint);
                    code.SetEntryUsage(sessionDescription, target, entry, shader.GetSlangUsedParameters(target, entry), arguments, entryPoint);
                }
            }
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

    internal async ValueTask<ShaderInputSource[]> ReadInputsAsync(ShaderSourceBlock source, CancellationToken cancellationToken)
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
                        new(input.Input, inputBlock.DataVersion, Id));
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
                        new(input.Input, data.DataVersion, Id));
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
