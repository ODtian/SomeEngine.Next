using System.Globalization;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Saved target and entry code. Direct Slang ownership and reflection are restored from the
/// sibling source block; saved chunks are materialized into immutable managed arrays.
/// </summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderBlock"), BinarySerializable]
[BinaryFields(nameof(ImportTrace), nameof(SchemaVersion))]
public sealed partial class ShaderBlock : AssetBlock
{
    public const uint Version = 10;

    public partial ImportTrace? ImportTrace { get; set; }
    public partial uint SchemaVersion { get; set; }

    partial void Initialize() => SchemaVersion = Version;

    public static ulong TargetCodeKey(SessionDesc sessionDescription, int targetIndex)
    {
        TargetDesc target = GetTarget(sessionDescription, targetIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.TargetCode." +
            CodeSettings(sessionDescription, target));
    }

    public static ulong EntryCodeKey(
        SessionDesc sessionDescription,
        int targetIndex,
        int entryIndex)
    {
        TargetDesc target = GetTarget(sessionDescription, targetIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.EntryCode." +
            CodeSettings(sessionDescription, target) + "." +
            entryIndex.ToString(CultureInfo.InvariantCulture));
    }

    public void SetTargetCode(
        SessionDesc sessionDescription,
        int targetIndex,
        ReadOnlySpan<byte> code) =>
        SetCode(TargetCodeKey(sessionDescription, targetIndex), code);

    public void SetEntryCode(
        SessionDesc sessionDescription,
        int targetIndex,
        int entryIndex,
        ReadOnlySpan<byte> code) =>
        SetCode(EntryCodeKey(sessionDescription, targetIndex, entryIndex), code);

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
        {
            return await source.CreateAsync(
                slang,
                sessionDescription,
                name,
                specializations,
                cancellationToken).ConfigureAwait(false);
        }

        using ShaderBlock copy = Duplicate();
        copy.ValidateContent();

        Shader shader = await source.CreateUsingSavedCodeAsync(
            slang,
            sessionDescription,
            name,
            specializations,
            cancellationToken).ConfigureAwait(false);
        for (int targetIndex = 0; targetIndex < shader.SlangTargetCount; targetIndex++)
        for (int entryIndex = 0; entryIndex < shader.SlangEntryCount; entryIndex++)
        {
            if (copy.ContentKeys.Contains(EntryCodeKey(
                    sessionDescription,
                    targetIndex,
                    entryIndex)))
            {
                continue;
            }

            return await source.CreateAsync(
                slang,
                sessionDescription,
                name,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var targetCode = new List<byte[]?>(shader.SlangTargetCount);
        var entryCode = new List<byte[]>(
            checked(shader.SlangTargetCount * shader.SlangEntryCount));
        for (int targetIndex = 0; targetIndex < shader.SlangTargetCount; targetIndex++)
        {
            ulong targetKey = TargetCodeKey(sessionDescription, targetIndex);
            targetCode.Add(copy.ContentKeys.Contains(targetKey)
                ? await AcquireCodeAsync(
                    copy,
                    targetKey,
                    cancellationToken).ConfigureAwait(false)
                : null);
            for (int entryIndex = 0; entryIndex < shader.SlangEntryCount; entryIndex++)
            {
                entryCode.Add(await AcquireCodeAsync(
                    copy,
                    EntryCodeKey(sessionDescription, targetIndex, entryIndex),
                    cancellationToken).ConfigureAwait(false));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return shader.WithManagedCode(targetCode, entryCode);
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

    private static string CodeSettings(SessionDesc sessionDescription, TargetDesc target)
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
