using System.Runtime.InteropServices;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Tools.SerializationAotSmoke;

internal static class Program
{
    private const int ContractDestinationCapacity = 75;
    private const int ContractPayloadLength = 6;
    private const int NativeBlockFixedHeaderSize = 48;
    private const int PlainChunkLength = 6;
    private const int CompressedChunkLength = 16 * 1024;
    private const ulong PlainChunkKey = 0x1001;
    private const ulong CompressedChunkKey = 0x2002;

    public static async Task<int> Main()
    {
        try
        {
            int serializedLength = RunContractViewSmoke();

            var catalog = new BinaryContractCatalog();
            global::SomeEngine.GeneratedContracts.Assembly_SomeEngine_Serialization_AotSmoke
                .GeneratedBinaryContractCatalog.RegisterAll(catalog);
            catalog.Freeze();
            Require(catalog.TryGet(SmokeContract.TypeId, out BinaryContractDescriptor generatedDescriptor),
                "Generated AOT catalog did not contain SmokeContract.");
            Require(generatedDescriptor.ContractType == typeof(SmokeContract),
                "Generated AOT catalog resolved the wrong contract type.");

            int nativeElementCount = RunNativeBlockSmoke();

            string documentPath = Path.Combine(
                Path.GetTempPath(),
                $"someengine-serialization-aot-{Guid.NewGuid():N}.seidx");
            long documentLength;
            Guid documentGeneration;
            try
            {
                documentLength = await WriteBinaryDocumentAsync(documentPath);
                documentGeneration = await ValidateBinaryDocumentAsync(documentPath, documentLength);
            }
            finally
            {
                if (File.Exists(documentPath))
                    File.Delete(documentPath);
            }

            Console.WriteLine(
                $"serialization-nativeaot-smoke:ok " +
                $"contractBytes={serializedLength} " +
                $"documentBytes={documentLength} " +
                $"plainChunkBytes={PlainChunkLength} " +
                $"brotliChunkBytes={CompressedChunkLength} " +
                $"nativeElements={nativeElementCount} " +
                $"generation={documentGeneration:N}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunContractViewSmoke()
    {
        SmokeContract expected = CreateExpected();
        byte[] serialized = GC.AllocateUninitializedArray<byte>(ContractDestinationCapacity);
        if (!BinaryContractSerializer.TryWrite(serialized, expected, out int serializedLength))
        {
            throw new InvalidOperationException(
                $"The fixed {ContractDestinationCapacity}-byte contract destination is too small; " +
                "the AOT smoke does not resize or retry a codec.");
        }

        var spanView = new SmokeContract.SpanView(serialized.AsSpan(0, serializedLength));
        spanView.Validate();
        Require(spanView.GetRevision() == expected.Revision, "Generated SpanView primitive getter changed.");
        Require(spanView.GetEnabled() == expected.Enabled, "Generated SpanView Boolean getter changed.");
        Require(spanView.GetAssetId() == expected.AssetId, "Generated SpanView Guid getter changed.");
        Require(spanView.TryGetNameUtf8(out ReadOnlySpan<byte> nameUtf8) &&
            nameUtf8.SequenceEqual("NativeAOT-序列化"u8),
            "Generated SpanView UTF-8 slice changed.");
        Require(spanView.TryGetPayloadBytes(out ReadOnlySpan<byte> payloadView) &&
            payloadView.SequenceEqual(expected.Payload!.Value.Span),
            "Generated SpanView blob slice changed.");
        Require(!spanView.GetPointsEncoded().IsEmpty, "Generated SpanView collection slice changed.");

        using BinaryContractViewOwner owner = BinaryContractViewOwner.Borrow(
            serialized.AsMemory(0, serializedLength));
        SmokeContract.View longView = SmokeContract.CreateView(owner);
        Require(longView.GetRevision() == expected.Revision,
            "Generated long-lived View primitive getter changed.");
        Require(longView.GetEnabled() == expected.Enabled,
            "Generated long-lived View Boolean getter changed.");
        Require(longView.GetAssetId() == expected.AssetId,
            "Generated long-lived View Guid getter changed.");
        Require(longView.TryGetNameUtf8(out ReadOnlySpan<byte> longNameUtf8) &&
            longNameUtf8.SequenceEqual("NativeAOT-序列化"u8),
            "Generated long-lived View UTF-8 slice changed.");
        Require(longView.TryGetPayloadBytes(out ReadOnlySpan<byte> longPayloadView) &&
            longPayloadView.SequenceEqual(expected.Payload!.Value.Span),
            "Generated long-lived View blob slice changed.");
        Require(!longView.GetPointsEncoded().IsEmpty,
            "Generated long-lived View collection slice changed.");
        return serializedLength;
    }

    private static int RunNativeBlockSmoke()
    {
        NativeLayoutProof<NativeVertex> proof = NativeVertex.NativeLayoutProof;
        NativeVertex[] expected =
        [
            new NativeVertex(1, 2, 3, 4),
            new NativeVertex(5, 6, 7, 8),
        ];
        int nativeBlockLength = GetNativeBlockLength(expected.Length, proof);
        byte[] nativeBuffer = GC.AllocateUninitializedArray<byte>(nativeBlockLength);
        if (!TryWriteNativeBlock(nativeBuffer, expected, proof, out int nativeWritten))
        {
            throw new InvalidOperationException(
                "The fixed native-block destination is too small; the AOT smoke does not resize or retry.");
        }
        return ValidateNativeBlock(nativeBuffer.AsSpan(0, nativeWritten), expected, proof);
    }

    private static async ValueTask<long> WriteBinaryDocumentAsync(string documentPath)
    {
        byte[] plainChunk = [4, 8, 15, 16, 23, 42];
        byte[] compressedChunk = new byte[CompressedChunkLength];
        for (int index = 0; index < compressedChunk.Length; index++)
            compressedChunk[index] = checked((byte)(index % 251));

        BinaryDocumentWriter documentBuilder = BinaryDocumentWriter.Create(CreateExpected())
            .AddChunk(
                PlainChunkKey,
                plainChunk,
                typeFingerprint: 0xABCDEF01,
                compression: ChunkCompression.None,
                alignment: 16)
            .AddChunk(
                CompressedChunkKey,
                compressedChunk,
                typeFingerprint: 0xABCDEF02,
                compression: ChunkCompression.Brotli,
                alignment: 64);

        await using var destination = new FileStream(
            documentPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        await documentBuilder.WriteAsync(destination);
        await destination.FlushAsync();
        return destination.Length;
    }

    private static async ValueTask<Guid> ValidateBinaryDocumentAsync(
        string documentPath,
        long documentLength)
    {
        SmokeContract.View leasedRootView;
        Guid generation;
        await using (BinaryDocumentView<SmokeContract, SmokeContract.View> rootViewDocument =
            await SmokeContract.OpenDocumentViewAsync(
                FileRangeSource.Open(documentPath),
                ownsSource: true))
        {
            leasedRootView = rootViewDocument.Root;
            leasedRootView.Validate();
            Require(leasedRootView.GetRevision() == 17,
                "Indexed generated root view primitive getter changed.");
            Require(leasedRootView.GetEnabled(),
                "Indexed generated root view Boolean getter changed.");
            Require(leasedRootView.GetAssetId() == Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                "Indexed generated root view Guid getter changed.");
            Require(leasedRootView.TryGetNameUtf8(out ReadOnlySpan<byte> indexedNameUtf8) &&
                indexedNameUtf8.SequenceEqual("NativeAOT-序列化"u8),
                "Indexed generated root view UTF-8 slice changed.");
            Require(leasedRootView.TryGetPayloadBytes(out ReadOnlySpan<byte> indexedPayloadView) &&
                IsExpectedPayload(indexedPayloadView),
                "Indexed generated root view blob slice changed.");
            Require(!leasedRootView.GetPointsEncoded().IsEmpty,
                "Indexed generated root view collection slice changed.");
            Require(rootViewDocument.TypeCatalog.Count == 1,
                "Indexed document type catalog count changed.");
            Require(rootViewDocument.TypeCatalog[0].TypeId == SmokeContract.TypeId,
                "Indexed document type catalog lost the root type id.");
            Require(rootViewDocument.ChunkCount == 2,
                "Indexed document chunk count changed.");
            Require(rootViewDocument.TotalLength == documentLength,
                "Indexed document length metadata changed.");
            generation = rootViewDocument.Generation;
        }

        bool disposedViewRejected = false;
        try
        {
            _ = leasedRootView.GetRevision();
        }
        catch (ObjectDisposedException)
        {
            disposedViewRejected = true;
        }
        Require(disposedViewRejected,
            "Indexed generated root view remained accessible after lease disposal.");
        return generation;
    }

    private static bool IsExpectedPayload(ReadOnlySpan<byte> payload)
        => payload.Length == ContractPayloadLength
            && payload[0] == 2
            && payload[1] == 3
            && payload[2] == 5
            && payload[3] == 7
            && payload[4] == 11
            && payload[5] == 13;

    private static SmokeContract CreateExpected()
        => new()
        {
            Revision = 17,
            Enabled = true,
            AssetId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Name = "NativeAOT-序列化",
            Payload = new byte[] { 2, 3, 5, 7, 11, 13 },
            Points =
            [
                new SmokePoint { X = 1.25f, Y = -2.5f },
                new SmokePoint { X = 3.75f, Y = 4.5f },
            ],
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int ValidateNativeBlock(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<NativeVertex> expected,
        in NativeLayoutProof<NativeVertex> proof)
    {
        ReadOnlySpan<NativeVertex> view = NativeBlock.Read<NativeVertex>(
            bytes,
            proof,
            maxElementCount: 1_024,
            out int consumedBytes);
        Require(view.SequenceEqual(expected), "Native raw Span view did not round-trip.");
        Require(consumedBytes == bytes.Length, "Native raw Span parser did not consume exactly one block.");
        return view.Length;
    }

    private static bool TryWriteNativeBlock<T>(
        Span<byte> destination,
        ReadOnlySpan<T> values,
        in NativeLayoutProof<T> proof,
        out int written)
        where T : unmanaged
    {
        int requiredLength = GetNativeBlockLength(values.Length, proof);
        if (destination.Length < requiredLength)
        {
            written = 0;
            return false;
        }

        var writer = new BinaryDataWriter(destination[..requiredLength]);
        NativeBlock.Write(ref writer, values, proof);
        written = writer.WrittenCount;
        return written == requiredLength;
    }

    private static int GetNativeBlockLength<T>(
        int elementCount,
        in NativeLayoutProof<T> proof)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        int payloadOffset = checked(
            (NativeBlockFixedHeaderSize + proof.Alignment - 1) & -proof.Alignment);
        return checked(payloadOffset + checked(elementCount * proof.Size));
    }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
internal sealed partial class SmokeContract
{
    public int Revision { get; set; }
    public bool Enabled { get; set; }
    public Guid AssetId { get; set; }
    public string? Name { get; set; }
    public Memory<byte>? Payload { get; set; }
    public IList<SmokePoint>? Points { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
internal partial struct SmokePoint
{
    public float X { get; set; }
    public float Y { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryNativeLayout("SomeEngine.NativeVertex.v1")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal partial struct NativeVertex
{
    public NativeVertex(int x, int y, int z, int w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public int X;
    public int Y;
    public int Z;
    public int W;
}
