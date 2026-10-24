using System.Globalization;
using System.Text;
using System.Buffers.Binary;
using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Saved target and entry code. Direct Slang ownership and reflection are restored from the
/// sibling source block; saved chunks are materialized into immutable managed arrays.
/// </summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderBlock"), BinarySerializable]
[BinaryFields(nameof(ImportTrace), nameof(SchemaVersion), nameof(InputVersions))]
public sealed partial class ShaderBlock : AssetBlock
{
    public const uint Version = 12;

    public partial ImportTrace? ImportTrace { get; set; }
    public partial uint SchemaVersion { get; set; }
    public partial IList<ShaderInputVersion> InputVersions { get; set; }

    partial void Initialize() => SchemaVersion = Version;

    public static ulong TargetCodeKey(SessionDesc sessionDescription, int targetIndex,
        ReadOnlySpan<string> arguments = default, string? entryPoint = null)
    {
        TargetDesc target = GetTarget(sessionDescription, targetIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.TargetCode." +
            CodeSettings(sessionDescription, target, arguments, entryPoint));
    }

    public static ulong EntryCodeKey(
        SessionDesc sessionDescription,
        int targetIndex,
        int entryIndex,
        ReadOnlySpan<string> arguments = default, string? entryPoint = null)
    {
        TargetDesc target = GetTarget(sessionDescription, targetIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.EntryCode." +
            CodeSettings(sessionDescription, target, arguments, entryPoint) + "." +
            entryIndex.ToString(CultureInfo.InvariantCulture));
    }

    public void SetTargetCode(
        SessionDesc sessionDescription,
        int targetIndex,
        ReadOnlySpan<byte> code,
        ReadOnlySpan<string> arguments = default, string? entryPoint = null) =>
        SetCode(TargetCodeKey(sessionDescription, targetIndex, arguments, entryPoint), code);

    public void SetEntryCode(
        SessionDesc sessionDescription,
        int targetIndex,
        int entryIndex,
        ReadOnlySpan<byte> code,
        ReadOnlySpan<string> arguments = default, string? entryPoint = null) =>
        SetCode(EntryCodeKey(sessionDescription, targetIndex, entryIndex, arguments, entryPoint), code);

    private static ulong EntryUsageKey(SessionDesc description, int target, int entry, ReadOnlySpan<string> arguments, string? entryPoint) =>
        BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Shader.EntryUsage." +
            CodeSettings(description, GetTarget(description, target), arguments, entryPoint) + "." +
            entry.ToString(CultureInfo.InvariantCulture));

    public void SetEntryUsage(SessionDesc description, int target, int entry,
        IReadOnlySet<SlangDescriptorLocation> used, ReadOnlySpan<string> arguments = default, string? entryPoint = null)
    {
        byte[] bytes = new byte[checked(4 + used.Count * 20)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, used.Count);
        int offset = 4;
        foreach (SlangDescriptorLocation location in used.OrderBy(value => value.Category)
            .ThenBy(value => value.Space).ThenBy(value => value.Register))
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), (int)location.Category);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 4), location.Space);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 12), location.Register);
            offset += 20;
        }
        SetCode(EntryUsageKey(description, target, entry, arguments, entryPoint), bytes);
    }

    private static ImmutableHashSet<SlangDescriptorLocation> ReadEntryUsage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new InvalidDataException("Shader resource usage data is incomplete.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || count != (bytes.Length - 4) / 20 || (bytes.Length - 4) % 20 != 0)
            throw new InvalidDataException("Shader resource usage data has an invalid length.");
        var result = ImmutableHashSet.CreateBuilder<SlangDescriptorLocation>();
        for (int offset = 4; offset < bytes.Length; offset += 20)
            result.Add(new((SlangParameterCategory)BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 12)..])));
        return result.ToImmutable();
    }

    public async ValueTask<Shader> CreateAsync(
        ShaderSourceBlock source,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!specializations.IsEmpty)
            throw new ArgumentException("Saved specialization arguments require an entry-point name.", nameof(specializations));
        using ShaderBlock copy = Duplicate();
        using ShaderSourceBlock inputs = source.Duplicate();
        copy.ValidateContent();
        Shader shader = await ReadVariantAsync(copy, inputs, slang, sessionDescription, name, null, [],
            cancellationToken).ConfigureAwait(false);
        if (inputs.Specializations.Count == 0) return shader;

        var savedSources = await inputs.ReadSavedSourcesAsync(cancellationToken).ConfigureAwait(false);
        SessionDesc description = CopySessionDescription(sessionDescription);
        var variants = new List<(string EntryPoint, string[] Arguments, Lazy<Shader> Shader)>(inputs.Specializations.Count);
        foreach (ShaderSpecialization specialization in inputs.Specializations)
        {
            string entryPoint = specialization.EntryPoint;
            string[] values = specialization.Arguments.ToArray();
            var code = await ReadCodeAsync(copy, shader, description, name, entryPoint, values,
                cancellationToken).ConfigureAwait(false);
            variants.Add((entryPoint, values, new Lazy<Shader>(() =>
                ShaderSourceBlock.RestoreSavedEntry(slang, description, name, entryPoint, values,
                    savedSources.Names, savedSources.Sources).WithManagedCode(code.Targets, code.Entries, code.Usage),
                LazyThreadSafetyMode.ExecutionAndPublication)));
        }
        return shader.WithSpecializations(variants);
    }

    public async ValueTask<Shader> CreateAsync(
        ShaderSourceBlock source,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        string entryPoint,
        ReadOnlyMemory<SpecializationArg> specializations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using ShaderBlock copy = Duplicate();
        using ShaderSourceBlock inputs = source.Duplicate();
        copy.ValidateContent();
        string[] arguments = ShaderSpecialization.Expressions(specializations.Span);
        if (!inputs.Specializations.Any(value =>
            value.EntryPoint == entryPoint && value.Arguments.SequenceEqual(arguments)))
            throw new InvalidDataException($"Shader '{name}' does not declare the requested entry specialization.");
        return await ReadVariantAsync(copy, inputs, slang, sessionDescription, name, entryPoint, arguments,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Shader> ReadVariantAsync(
        ShaderBlock copy,
        ShaderSourceBlock source,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        string? entryPoint,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        Shader shader = await source.CreateUsingSavedCodeAsync(slang, sessionDescription, name,
            arguments.Select(SpecializationArg.FromExpression).ToArray(), cancellationToken, entryPoint).ConfigureAwait(false);
        var code = await ReadCodeAsync(copy, shader, sessionDescription, name, entryPoint, arguments,
            cancellationToken).ConfigureAwait(false);
        return shader.WithManagedCode(code.Targets, code.Entries, code.Usage);
    }

    private static async ValueTask<(byte[]?[] Targets, byte[][] Entries, ImmutableHashSet<SlangDescriptorLocation>[] Usage)>
        ReadCodeAsync(ShaderBlock copy, Shader shader, SessionDesc sessionDescription, string name,
            string? entryPoint, string[] arguments, CancellationToken cancellationToken)
    {
        int entryCount = entryPoint is null ? shader.SlangEntryCount :
            Enumerable.Range(0, shader.SlangEntryCount).Count(index =>
                shader.GetSlangEntryPoint(index).GetFunctionReflection().Name == entryPoint);
        if (entryCount == 0)
            throw new InvalidDataException($"Shader '{name}' declares a specialization for an absent entry point.");
        var targetCode = new byte[]?[shader.SlangTargetCount];
        var entryCode = new byte[checked(shader.SlangTargetCount * entryCount)][];
        var usedParameters = new ImmutableHashSet<SlangDescriptorLocation>[entryCode.Length];
        for (int targetIndex = 0; targetIndex < shader.SlangTargetCount; targetIndex++)
        {
            ulong targetKey = TargetCodeKey(sessionDescription, targetIndex, arguments, entryPoint);
            targetCode[targetIndex] = copy.ContentKeys.Contains(targetKey)
                ? await AcquireCodeAsync(copy, targetKey, cancellationToken).ConfigureAwait(false)
                : null;
            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                int index = targetIndex * entryCount + entryIndex;
                if (entryPoint is null && shader.GetSlangEntryPoint(entryIndex).GetSpecializationParamCount() != 0)
                {
                    entryCode[index] = [];
                    usedParameters[index] = ImmutableHashSet<SlangDescriptorLocation>.Empty;
                    continue;
                }
                ulong codeKey = EntryCodeKey(sessionDescription, targetIndex, entryIndex, arguments, entryPoint);
                ulong usageKey = EntryUsageKey(sessionDescription, targetIndex, entryIndex, arguments, entryPoint);
                if (!copy.ContentKeys.Contains(codeKey))
                    throw new InvalidDataException($"Shader '{name}' is missing cooked code for its target and entry specialization.");
                if (!copy.ContentKeys.Contains(usageKey))
                    throw new InvalidDataException("Saved shader entry code requires its cooked resource usage metadata.");
                entryCode[index] = await AcquireCodeAsync(copy, codeKey, cancellationToken).ConfigureAwait(false);
                usedParameters[index] = ReadEntryUsage(await AcquireCodeAsync(copy, usageKey, cancellationToken).ConfigureAwait(false));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (targetCode, entryCode, usedParameters);
    }

    private static SessionDesc CopySessionDescription(SessionDesc description)
    {
        description.SearchPaths = description.SearchPaths?.ToArray();
        description.PreprocessorMacros = description.PreprocessorMacros?.ToArray();
        description.CompilerOptionEntries = description.CompilerOptionEntries?.ToArray();
        description.Targets = description.Targets?.Select(static target =>
        {
            target.CompilerOptionEntries = target.CompilerOptionEntries?.ToArray();
            return target;
        }).ToArray();
        return description;
    }

    internal bool MatchesInputs(IEnumerable<ShaderInputVersion> inputs, SessionDesc description) =>
        SchemaVersion == Version && InputVersions.SequenceEqual(inputs);

    internal ValueTask<Shader> ReadCodeAsync(Shader shader, SessionDesc description, CancellationToken cancellationToken) =>
        ReadManagedCodeAsync(shader, description, cancellationToken);

    private async ValueTask<Shader> ReadManagedCodeAsync(Shader shader, SessionDesc description,
        CancellationToken cancellationToken)
    {
        var code = await ReadCodeAsync(this, shader, description, shader.Name, null, [], cancellationToken).ConfigureAwait(false);
        return shader.WithManagedCode(code.Targets, code.Entries, code.Usage);
    }

    protected override void ValidateContent()
    {
        if (SchemaVersion != Version)
            throw new InvalidDataException("The shader block schema version is unsupported.");
    }

    protected override async ValueTask WriteChunksAsync(
        AssetBlockWriter writer,
        CancellationToken cancellationToken)
    {
        using AssetBlockSource? source = RetainSource();
        uint ordinal = 0;
        foreach (ulong key in ContentKeys.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Chunks.ContainsKey(key))
            {
                writer.WriteChunk(
                    key,
                    Chunks[key],
                    ChunkCompression.Brotli,
                    ordinal: ordinal++);
            }
            else
            {
                await writer.CopyChunkAsync(
                    source ?? throw new InvalidDataException("Shader code has no content source."),
                    key,
                    cancellationToken,
                    ordinal: ordinal++).ConfigureAwait(false);
            }
        }
    }

    private void SetCode(ulong key, ReadOnlySpan<byte> code)
    {
        if (code.IsEmpty)
            throw new ArgumentException("Shader code cannot be empty.", nameof(code));
        byte[] storage = code.ToArray();
        lock (SyncRoot)
        {
            RequireIdle();
            SetBytes(key, storage);
        }
    }

    private static TargetDesc GetTarget(SessionDesc sessionDescription, int targetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        if (sessionDescription.Targets is not { } targets || targetIndex >= targets.Length)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        return targets[targetIndex];
    }

    private static string CodeSettings(SessionDesc sessionDescription, TargetDesc target, ReadOnlySpan<string> arguments, string? entryPoint)
    {
        var text = new StringBuilder(256);
        AppendUInt(text, (uint)sessionDescription.Flags);
        AppendUInt(text, (uint)sessionDescription.DefaultMatrixLayoutMode);
        AppendUInt(text, sessionDescription.EnableEffectAnnotations ? 1u : 0u);
        AppendUInt(text, sessionDescription.AllowGLSLSyntax ? 1u : 0u);
        AppendUInt(text, sessionDescription.SkipSPIRVValidation ? 1u : 0u);
        AppendUInt(text, (uint)target.Format);
        AppendUInt(text, (uint)target.Profile);
        AppendUInt(text, (uint)target.Flags);
        AppendUInt(text, (uint)target.FloatingPointMode);
        AppendUInt(text, (uint)target.LineDirectiveMode);
        AppendUInt(text, target.ForceGLSLScalarBufferLayout ? 1u : 0u);
        AppendMacros(text, sessionDescription.PreprocessorMacros);
        AppendOptions(text, sessionDescription.CompilerOptionEntries);
        AppendOptions(text, target.CompilerOptionEntries);
        AppendString(text, entryPoint);
        AppendUInt(text, checked((uint)arguments.Length));
        foreach (string argument in arguments) AppendString(text, argument);
        return text.ToString();

        static void AppendUInt(StringBuilder text, uint value) =>
            text.Append(value.ToString(CultureInfo.InvariantCulture)).Append(':');

        static void AppendString(StringBuilder text, string? value)
        {
            value ??= string.Empty;
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append('#')
                .Append(value)
                .Append(':');
        }

        static void AppendMacros(StringBuilder text, PreprocessorMacroDesc[]? macros)
        {
            AppendUInt(text, checked((uint)(macros?.Length ?? 0)));
            foreach (PreprocessorMacroDesc macro in macros ?? [])
            {
                AppendString(text, macro.Name);
                AppendString(text, macro.Value);
            }
        }

        static void AppendOptions(StringBuilder text, CompilerOptionEntry[]? options)
        {
            AppendUInt(text, checked((uint)(options?.Length ?? 0)));
            foreach (CompilerOptionEntry option in options ?? [])
            {
                AppendUInt(text, (uint)option.Name);
                AppendUInt(text, (uint)option.Value.Kind);
                AppendUInt(text, unchecked((uint)option.Value.IntValue0));
                AppendUInt(text, unchecked((uint)option.Value.IntValue1));
                AppendString(text, option.Value.StringValue0);
                AppendString(text, option.Value.StringValue1);
            }
        }
    }

    private static async ValueTask<byte[]> AcquireCodeAsync(
        ShaderBlock block,
        ulong key,
        CancellationToken cancellationToken)
    {
        if (!block.ContentKeys.Contains(key))
            throw new InvalidDataException($"Shader code chunk '{key}' is missing.");
        ReadOnlyMemory<byte> code = await block.ReadBytesAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (code.Length == 0)
            throw new InvalidDataException($"Shader code chunk '{key}' is empty.");
        if (code.Length > Array.MaxLength)
            throw new InvalidDataException($"Shader code chunk '{key}' exceeds managed array limits.");
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(code, out ArraySegment<byte> data)
            && data.Offset == 0 && data.Count == data.Array!.Length) return data.Array;
        return code.ToArray();
    }
}

[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Input), nameof(DataVersion))]
public readonly partial record struct ShaderInputVersion(ShaderInputLocation Input, Guid DataVersion);
