using System.Globalization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Shader
{
    internal static async ValueTask<Shader> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using BinaryDocument<Shader> document =
            await AssetProject.OpenAsync<Shader>(path, cancellationToken)
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

    internal static BinaryDocumentWriter CreateWriter(Shader asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        int programCount = asset.Programs?.Count ?? 0;
        for (int index = 0; index < programCount; index++)
        {
            CookedShaderProgram program = asset.Programs![index];
            Memory<byte>? payload = program.Data;
            if (!payload.HasValue || payload.Value.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Cooked shader program {index} must provide one externalized payload.");
            }
            program.DataChunkKey = ProgramChunkKey(index);
            program.DataDecodedLength = checked((ulong)payload.Value.Length);
        }

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(asset);
        for (int index = 0; index < programCount; index++)
        {
            builder.AddChunk(
                asset.Programs![index].DataChunk.Key,
                asset.Programs![index].Data!.Value,
                AssetMetadata.RawBytesTypeFingerprint,
                ChunkCompression.Brotli,
                alignment: 16,
                ordinal: checked((uint)index));
        }

        return builder;
    }

    /// <summary>Reads every bytecode variant into its final managed backing.</summary>
    internal static async ValueTask<Shader> MaterializeAsync(
        BinaryDocument<Shader> document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateExternalizedRoot(document.Root);
        Shader result = document.Root;
        int programCount = result.Programs?.Count ?? 0;
        for (int index = 0; index < programCount; index++)
        {
            Memory<byte>? payload = await document.TryReadChunkAsync(
                result.Programs![index].DataChunk,
                static length => GC.AllocateUninitializedArray<byte>(length),
                cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue)
            {
                throw new InvalidDataException(
                    $"Shader document is missing required bytecode chunk {index}.");
            }
            CookedShaderProgram cooked = result.Programs![index];
            cooked.Data = payload;
            cooked.Program = SlangShaderSharp.ShaderProgram.Read(payload.Value.Span);
            if (!string.Equals(cooked.Backend, cooked.Program.Backend, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cooked shader program {index} backend '{cooked.Backend}' does not match " +
                    $"its detached payload backend '{cooked.Program.Backend}'.");
            }
        }

        return result;
    }

    private static void ValidateExternalizedRoot(Shader root)
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
