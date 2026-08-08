using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class RangeSourceAndNativeBlockTests
{
    [Fact]
    public async Task FileRangeSourcePerformsConcurrentExplicitOffsetReads()
    {
        byte[] contents = Enumerable.Range(0, 4096)
            .Select(static value => (byte)(value * 17 + 3))
            .ToArray();
        string path = Path.Combine(AppContext.BaseDirectory, $"range-source-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, contents);

        try
        {
            await using FileRangeSource source = FileRangeSource.Open(path);
            byte[] first = new byte[73];
            byte[] second = new byte[129];

            await Task.WhenAll(
                source.ReadExactlyAsync(1900, first).AsTask(),
                source.ReadExactlyAsync(37, second).AsTask());
            using RangeLease lease = await source.AcquireAsync(3000, 211);

            Assert.Equal(contents.AsSpan(1900, first.Length).ToArray(), first);
            Assert.Equal(contents.AsSpan(37, second.Length).ToArray(), second);
            Assert.Equal(contents.AsSpan(3000, 211).ToArray(), lease.Memory.ToArray());
            Assert.Equal(contents.Length, source.Length);
            Assert.StartsWith("file:", source.Generation, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NativeBlockRoundTripsAndRejectsLayoutFingerprintMismatch()
    {
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        int[] values = [int.MinValue, -1, 0, 17, int.MaxValue];
        GeneratedNativeInt32[] nativeValues = Wrap(values);
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        NativeBlock.Write(ref writer, nativeValues, proof);
        bytes.SetLength(writer.WrittenCount);

        Assert.True(NativeBlock.IsSupported(proof));
        Assert.Equal(values, ReadNativeInts(bytes, proof));

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Span, proof.Fingerprint + 1);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ReadNativeInts(bytes, proof));
        Assert.Contains("layout fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeBlockRejectsRuntimeElementSizeMismatch()
    {
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        NativeBlock.Write<GeneratedNativeInt32>(ref writer, Wrap([1, 2, 3]), proof);
        bytes.SetLength(writer.WrittenCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Span.Slice(8, 4), sizeof(long));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ReadNativeInts(bytes, proof));

        Assert.Contains("element size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeBlockRejectsAbiMetadataThatDoesNotMatchProof()
    {
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        NativeBlock.Write<GeneratedNativeInt32>(ref writer, Wrap([1, 2]), proof);
        bytes.SetLength(writer.WrittenCount);
        bytes[20] ^= 0x40; // architecture token

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ReadNativeInts(bytes, proof));

        Assert.Contains("ABI metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedNativeLayoutProofCoversTheEntireRuntimeValue()
    {
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;

        Assert.NotEqual(0UL, proof.Fingerprint);
        Assert.Equal(sizeof(int), proof.Size);
        Assert.Equal(proof.Size, proof.CoveredFieldBytes);
        Assert.Equal(sizeof(int), proof.Alignment);
        Assert.True(NativeBlock.IsSupported(proof));
    }

    [Fact]
    public void NativeBlockSpanViewAllocatesNoManagedMemoryAfterWarmup()
    {
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        GeneratedNativeInt32[] values = Wrap([3, 5, 7, 11, 13]);
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        NativeBlock.Write(ref writer, values, proof);
        bytes.SetLength(writer.WrittenCount);

        _ = SumNativeSpanViews(bytes, proof, iterations: 32);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = SumNativeSpanViews(bytes, proof, iterations: 1_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(39_000, sum);
        Assert.Equal(0, allocated);
        GC.KeepAlive(bytes);
    }

    [Fact]
    public void NativeBlockWriterAccountsForNonzeroPrefixWhenAligningWirePayload()
    {
        const int prefixLength = 3;
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        writer.WriteBytes([0xA1, 0xB2, 0xC3]);
        NativeBlock.Write<GeneratedNativeInt32>(ref writer, Wrap([17, 29]), proof);
        bytes.SetLength(writer.WrittenCount);
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span.Slice(prefixLength + 36, 4));

        Assert.Equal(0, (prefixLength + payloadOffset) & (proof.Alignment - 1));
        Assert.InRange(payloadOffset - 48, 0, proof.Alignment - 1);
        foreach (byte value in bytes.Span.Slice(prefixLength + 48, payloadOffset - 48))
            Assert.Equal(0, value);

        var reader = new BinaryDataReader(bytes.Span);
        _ = reader.ReadBytes(prefixLength);
        ReadOnlySpan<GeneratedNativeInt32> actual = NativeBlock.Read<GeneratedNativeInt32>(ref reader, proof);
        Assert.Equal(17, actual[0].Value);
        Assert.Equal(29, actual[1].Value);
        reader.EnsureFullyConsumed("prefixed native block");
    }

    [Fact]
    public void NativeBlockRejectsNonzeroWirePadding()
    {
        const int prefixLength = 3;
        NativeLayoutProof<GeneratedNativeInt32> proof = GeneratedNativeInt32.NativeLayoutProof;
        OwnedTestEncoding bytes = OwnedTestEncoding.Allocate(256);
        var writer = new BinaryDataWriter(bytes.Memory.Span);
        writer.WriteBytes([0xA1, 0xB2, 0xC3]);
        NativeBlock.Write<GeneratedNativeInt32>(ref writer, Wrap([41]), proof);
        bytes.SetLength(writer.WrittenCount);
        int payloadOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span.Slice(prefixLength + 36, 4));
        Assert.True(payloadOffset > 48);
        bytes[prefixLength + 48] = 0x7F;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new BinaryDataReader(bytes.Span);
            _ = reader.ReadBytes(prefixLength);
            _ = NativeBlock.Read<GeneratedNativeInt32>(ref reader, proof);
        });

        Assert.Contains("padding", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int[] ReadNativeInts(
        OwnedTestEncoding bytes,
        in NativeLayoutProof<GeneratedNativeInt32> proof)
    {
        ReadOnlySpan<GeneratedNativeInt32> values = NativeBlock.Read<GeneratedNativeInt32>(
            bytes.Span,
            proof,
            maxElementCount: 1_024,
            out int consumedBytes);
        Assert.Equal(bytes.Length, consumedBytes);
        int[] result = new int[values.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = values[index].Value;
        return result;
    }

    private static int SumNativeSpanViews(
        OwnedTestEncoding bytes,
        in NativeLayoutProof<GeneratedNativeInt32> proof,
        int iterations)
    {
        int sum = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            ReadOnlySpan<GeneratedNativeInt32> values = NativeBlock.Read<GeneratedNativeInt32>(
                bytes.Span,
                proof,
                maxElementCount: 1_024,
                out int consumedBytes);
            if (consumedBytes != bytes.Length)
                throw new InvalidDataException("Native block parser did not consume exactly one block.");
            for (int index = 0; index < values.Length; index++)
                sum = unchecked(sum + values[index].Value);
        }
        return sum;
    }

    private static GeneratedNativeInt32[] Wrap(int[] values)
    {
        GeneratedNativeInt32[] result = new GeneratedNativeInt32[values.Length];
        for (int index = 0; index < values.Length; index++)
            result[index].Value = values[index];
        return result;
    }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
[BinaryNativeLayout("SomeEngine.Serialization.Tests.GeneratedNativeInt32.v1")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct GeneratedNativeInt32
{
    public int Value;
}
