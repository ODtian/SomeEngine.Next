using System.Globalization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

internal partial class ShaderData
{
    private static ValueTask<BinaryDocument> OpenDocumentAsync(string path, CancellationToken cancellationToken)
        => BinaryDocument.OpenAsync(SomeEngine.Serialization.IO.FileRangeSource.Open(path), Shader.Asset.WireType,
            ownsSource: true, cancellationToken: cancellationToken);

    internal static async ValueTask<ShaderData> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using BinaryDocument document =
            await OpenDocumentAsync(path, cancellationToken)
                .ConfigureAwait(false);
        return await MaterializeAsync(document, cancellationToken).ConfigureAwait(false);
    }

    internal static ulong ProgramChunkKey(int programIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(programIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.Programs.Data." +
            programIndex.ToString(CultureInfo.InvariantCulture));
    }

    internal static BinaryDocumentWriter CreateWriter(ShaderData asset)
        => CreateWriterAsync(asset, null, default).GetAwaiter().GetResult();

    internal static async ValueTask<BinaryDocumentWriter> CreateWriterAsync(ShaderData asset,
        BinaryDocument? source, CancellationToken cancellationToken)
    {
        ImmutableList<CookedShaderProgram> codes = asset.Programs ?? [];
        ImmutableList<CookedShaderProgram>.Builder? changed = null;
        BinaryDocumentWriter writer = BinaryDocumentWriter.Create(asset);
        int index = 0;
        foreach (CookedShaderProgram code in codes)
        {
            ulong key = ProgramChunkKey(index);
            ulong length = code.Data is { } bytes ? checked((ulong)bytes.Length) : code.DataDecodedLength;
            if (code.Data is { IsEmpty: false } payload)
                writer.AddChunk(key, payload, global::SomeEngine.Assets.Asset<Shader>.ByteSchema,
                    ChunkCompression.Brotli, ordinal: checked((uint)index));
            else if (source is not null && code.Data is null)
                await writer.AddChunkAsync(key, source, cancellationToken,
                    code.DataChunkKey, checked((uint)index)).ConfigureAwait(false);
            else
                throw new InvalidDataException($"Shader bytecode {index} has no payload.");
            if (key != code.DataChunkKey || length != code.DataDecodedLength)
            {
                changed ??= codes.ToBuilder();
                changed[index] = code with { DataChunkKey = key, DataDecodedLength = length };
            }
            index++;
        }
        if (changed is not null) asset.Programs = changed.ToImmutable();
        return writer;
    }

    /// <summary>Reads every bytecode variant into its final managed backing.</summary>
    internal static async ValueTask<ShaderData> MaterializeAsync(
        BinaryDocument? document,
        CancellationToken cancellationToken,
        string? backend = null,
        ShaderData? fields = null)
    {
        ShaderData result = fields?.CopyFields() ?? document!.ReadRoot<ShaderData>();
        if (fields is null) ValidateExternalizedRoot(result);
        ImmutableList<CookedShaderProgram> codes = result.Programs ?? [];
        ImmutableList<CookedShaderProgram>.Builder? changed = null;
        for (int index = 0; index < codes.Count; index++)
        {
            CookedShaderProgram cooked = codes[index];
            if (backend is not null && cooked.Backend != backend) continue;
            if (cooked.Program is not null) continue;
            ReadOnlyMemory<byte>? payload = cooked.Data;
            if (!payload.HasValue && document is not null)
                payload = await document.TryReadChunkAsync(cooked.DataChunk,
                    static length => GC.AllocateUninitializedArray<byte>(length), cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue)
            {
                throw new InvalidDataException(
                    $"ShaderData document is missing required bytecode chunk {index}.");
            }
            var decoded = SlangShaderSharp.ShaderProgram.Read(payload.Value.Span);
            if (!string.Equals(cooked.Backend, decoded.Backend, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cooked shader program {index} backend '{cooked.Backend}' does not match " +
                    $"its detached payload backend '{decoded.Backend}'.");
            }
            changed ??= codes.ToBuilder();
            changed[index] = cooked with { Data = payload, Program = decoded };
        }
        if (changed is not null) result.Programs = changed.ToImmutable();
        return result;
    }

    private static void ValidateExternalizedRoot(ShaderData root)
    {
        IList<CookedShaderProgram>? programs = root.Programs;
        if (programs is null)
            return;
        for (int index = 0; index < programs.Count; index++)
        {
            if (programs[index].Data.HasValue)
            {
                throw new InvalidDataException(
                    $"Binary shader root program {index} must not contain inline data.");
            }
            ulong expectedKey = ProgramChunkKey(index);
            if (programs[index].DataChunk.Key != expectedKey)
            {
                throw new InvalidDataException(
                    $"Binary shader root program {index} declares chunk key " +
                    $"0x{programs[index].DataChunk.Key:X16}; expected 0x{expectedKey:X16}.");
            }
        }
    }
}
