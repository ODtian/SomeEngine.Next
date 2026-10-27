using System.Diagnostics;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>A shader identity containing independent source and compiled-code blocks.</summary>
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Shader")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ShaderAsset : Asset
{
    private static readonly ActivitySource s_compilationActivities = new("SomeEngine.Shaders");
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
    private ShaderAsset(AssetEntry description) : base(description.AssetGuid) => AssetType = FileType;
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
        if (source.Inputs.Count == 0 && !source.HasSerializedSources)
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
        using ShaderSourceBlock selected = source.FromInputs(inputs);
        if (savedCode is not null && await savedCode.TryCreateAsync(selected, slang, sessionDescription,
            copy.Name, cancellationToken).ConfigureAwait(false) is { } saved)
            return saved;
        return await selected.CreateAsync(slang, sessionDescription, copy.Name, specializations,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores saved code for the complete selected input set; never compiles source code.</summary>
    /// <remarks>Missing or stale saved inputs/code are errors. Explicit specializations require an entry point.</remarks>
    public async ValueTask<Shader> CreateSavedAsync(
        IGlobalSession slang, SessionDesc sessionDescription, string? entryPoint = null,
        ReadOnlyMemory<SpecializationArg> specializations = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slang);
        cancellationToken.ThrowIfCancellationRequested();
        if (entryPoint is null && !specializations.IsEmpty)
            throw new ArgumentException("Saved specialization arguments require an entry-point name.", nameof(entryPoint));
        if (entryPoint is not null) ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        using ShaderAsset copy = Duplicate();
        if (!copy.ContainsBlock<ShaderSourceBlock>() || !copy.ContainsBlock<ShaderBlock>())
            throw new InvalidDataException("The shader has no saved source/code blocks.");
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        if (source.Inputs.Count == 0 && !source.HasSerializedSources)
            throw new InvalidDataException("The shader has no saved serialized inputs.");
        ShaderInputSource[] inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        if (inputs.Length == 0 || inputs.Any(static input => input.Serialized.IsEmpty))
            throw new InvalidDataException("The shader has incomplete saved serialized inputs.");
        ShaderBlock code = await copy.GetBlockAsync<ShaderBlock>(cancellationToken).ConfigureAwait(false);
        if (!code.MatchesInputs(inputs.Select(static input => input.Version), sessionDescription))
            throw new InvalidDataException("The saved shader code does not match its selected inputs.");
        using ShaderSourceBlock selected = source.FromInputs(inputs);
        return entryPoint is null
            ? await code.CreateAsync(selected, slang, sessionDescription, copy.Name,
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : await code.CreateAsync(selected, slang, sessionDescription, copy.Name, entryPoint,
                specializations, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles the asset's complete inputs and saves their common target and entry code.</summary>
    public async ValueTask<ShaderCompilation> CompileAsync(
        IGlobalSession slang, SessionDesc sessionDescription, CancellationToken cancellationToken = default)
    {
        using var activity = s_compilationActivities.StartActivity("Shader.Compile");
        using ShaderAsset copy = Duplicate();
        activity?.SetTag("asset", copy.Id.ToString("N"));
        activity?.SetTag("name", copy.Name);
        ShaderSourceBlock source = await copy.GetBlockAsync<ShaderSourceBlock>(cancellationToken).ConfigureAwait(false);
        var sourceFiles = new ShaderSourceFiles(sessionDescription.FileSystem);
        using var trackedInputs = sourceFiles.TrackInputVersions();
        ShaderCompilation compiled;
        ShaderInputSource[] inputs;
        if (source.Inputs.Count == 0 && !source.HasSerializedSources)
        {
            compiled = await source.CompileAsync(slang, sessionDescription, copy.Name, sourceFiles,
                cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < compiled.SerializedSources.Count; index++)
                source.SetSerializedSource(index, compiled.SerializedSources[index].Span);
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
            compiled = await source.CompileSelectedAsync(inputs, slang, sessionDescription, copy.Name,
                cancellationToken, sourceFiles).ConfigureAwait(false);
        }
        bool[] baseEntryHasSpecializationParameters = await ShaderSourceBlock.ReadEntrySpecializationFlagsAsync(
            slang, compiled.Shader, cancellationToken).ConfigureAwait(false);
        using var code = new ShaderBlock();
        SaveCode(compiled.Shader, [], null);
        source.Specializations = compiled.Specializations.ToList();
        inputs = await copy.ReadInputsAsync(source, cancellationToken).ConfigureAwait(false);
        code.InputVersions = inputs.Select(static input => input.Version).ToList();
        var requests = compiled.Specializations.Select(specialization =>
            (specialization.EntryPoint, Arguments: specialization.Arguments.ToArray())).ToArray();
        // Compilation and source snapshot ownership stay here. Variants share their loaded base graph.
        var batch = await ShaderSourceBlock.CompileUsingLoadedSourcesBatchAsync(slang, compiled.Shader, sourceFiles,
            requests.Select(static request => (request.EntryPoint,
                (ReadOnlyMemory<SpecializationArg>)request.Arguments.Select(SpecializationArg.FromExpression).ToArray())).ToArray(),
            cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < requests.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCode(batch.Shaders[index], requests[index].Arguments, requests[index].EntryPoint);
        }

        void SaveCode(Shader shader, string[] arguments, string? entryPoint)
        {
            bool hasUnboundEntries = entryPoint is null && baseEntryHasSpecializationParameters.Any(static value => value);
            for (int target = 0; target < shader.SlangTargetCount; target++)
            {
                if (!hasUnboundEntries && shader.GetSlangTargetBlob(target) is { } targetCode)
                    code.SetTargetCode(sessionDescription, target, targetCode.Buffer, arguments, entryPoint);
                for (int entry = 0; entry < shader.SlangEntryCount; entry++)
                {
                    if (entryPoint is null && baseEntryHasSpecializationParameters[entry]) continue;
                    if (shader.GetSlangEntryBlob(target, entry) is { } entryCode)
                        code.SetEntryCode(sessionDescription, target, entry, entryCode.Buffer, arguments, entryPoint);
                    code.SetEntryUsage(sessionDescription, target, entry, shader.GetSlangUsedParameters(target, entry), arguments, entryPoint);
                }
            }
        }
        // Include callbacks made during detached code/usage saving, not only the base module load.
        ShaderSourceFile[] files = compiled.Files.Concat(batch.Files).Concat(sourceFiles.Finish())
            .GroupBy(static file => file.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(static group => group.First()).ToArray();
        sourceFiles.ThrowIfInputChanged();
        cancellationToken.ThrowIfCancellationRequested();
        lock (SyncRoot)
        {
            SetBlock(source);
            SetBlock(code);
        }
        return new(compiled.Shader, compiled.SerializedSources, files) { Specializations = compiled.Specializations };
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
        using var activity = s_compilationActivities.StartActivity("Shader.Save");
        activity?.SetTag("path", path);
        using ShaderAsset copy = Duplicate();
        activity?.SetTag("asset", copy.Id.ToString("N"));
        return await copy.SaveBlockContentAsync(path, PathSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ShaderAsset> ReadFileAsync(AssetFile file, Guid expected, CancellationToken cancellationToken)
    {
        AssetEntry description = file.Describe();
        if ((expected != Guid.Empty && expected != description.AssetGuid) || description.AssetType != FileType)
            throw new InvalidDataException("The asset identity or type differs from the selected file.");
        var result = new ShaderAsset(description);
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
