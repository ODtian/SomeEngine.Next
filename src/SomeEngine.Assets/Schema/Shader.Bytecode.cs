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

    internal static ulong BytecodeChunkKey(int variantIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(variantIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.Variants.Data." +
            variantIndex.ToString(CultureInfo.InvariantCulture));
    }

    internal static BinaryDocumentWriter CreateWriter(Shader asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        int variantCount = asset.Variants?.Count ?? 0;
        for (int index = 0; index < variantCount; index++)
        {
            ShaderBytecode variant = asset.Variants![index];
            Memory<byte>? payload = variant.Data;
            if (!payload.HasValue || payload.Value.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Shader bytecode variant {index} must provide one externalized payload.");
            }
            variant.DataChunkKey = BytecodeChunkKey(index);
            variant.DataDecodedLength = checked((ulong)payload.Value.Length);
        }

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(asset);
        for (int index = 0; index < variantCount; index++)
        {
            builder.AddChunk(
                asset.Variants![index].DataChunk.Key,
                asset.Variants![index].Data!.Value,
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
        int variantCount = result.Variants?.Count ?? 0;
        for (int index = 0; index < variantCount; index++)
        {
            Memory<byte>? payload = await document.TryReadChunkAsync(
                result.Variants![index].DataChunk,
                static length => GC.AllocateUninitializedArray<byte>(length),
                cancellationToken).ConfigureAwait(false);
            if (!payload.HasValue)
            {
                throw new InvalidDataException(
                    $"Shader document is missing required bytecode chunk {index}.");
            }
            result.Variants![index].Data = payload;
        }

        return result;
    }

    private static void ValidateExternalizedRoot(Shader root)
    {
        IList<ShaderBytecode>? variants = root.Variants;
        if (variants is null)
            return;
        for (int index = 0; index < variants.Count; index++)
        {
            if (variants[index].Data.HasValue)
            {
                throw new InvalidDataException(
                    $"Binary shader root variant {index} must not contain inline bytecode.");
            }
            ulong expectedKey = BytecodeChunkKey(index);
            if (variants[index].DataChunk.Key != expectedKey)
            {
                throw new InvalidDataException(
                    $"Binary shader root variant {index} declares chunk key " +
                    $"0x{variants[index].DataChunk.Key:X16}; expected 0x{expectedKey:X16}.");
            }
        }
    }
}
