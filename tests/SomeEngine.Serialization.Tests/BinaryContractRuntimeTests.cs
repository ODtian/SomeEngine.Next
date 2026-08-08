using SomeEngine.Serialization;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryContractRuntimeTests
{
    [Fact]
    public void ManualContractUsesCanonicalLittleEndianPrimitiveAndUtf8Layout()
    {
        TestRoot value = TestRoots.Canonical();

        byte[] expected =
        [
            0x01,
            0xAB,
            0xFE,
            0x34, 0x12,
            0xCD, 0xAB,
            0x78, 0x56, 0x34, 0x12,
            0xEF, 0xCD, 0xAB, 0x90,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88,
            0x00, 0x00, 0x80, 0x3F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0xC0,
            0x34, 0x6C,
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
            0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
            0x03, 0x00, 0x00, 0x00,
            0x41, 0xC3, 0xA9, 0xE6, 0xB0, 0xB4,
            0x03, 0x00, 0x00, 0x00,
            0x01, 0x02, 0x03,
        ];
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(value, expected.Length);

        Assert.True(expected.AsSpan().SequenceEqual(encoded.Span));

        TestRoot decoded = BinaryContractSerializer.Deserialize<TestRoot>(encoded.Span);
        TestRoots.AssertEquivalent(value, decoded);
    }

    [Fact]
    public void TryWriteRejectsUndersizedDestinationWithoutRetryingTheCodec()
    {
        TestRoot value = TestRoots.Canonical();
        byte[] destination = Enumerable.Repeat((byte)0xCC, 77).ToArray();

        Assert.False(BinaryContractSerializer.TryWrite(destination, value, out int written));

        Assert.Equal(0, written);
        Assert.Contains(destination, value => value != 0xCC);
    }

    [Fact]
    public void NullableStringRoundTripsWithoutAmbiguity()
    {
        TestRoot value = TestRoots.Canonical(text: null);

        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(value, 256);
        TestRoot decoded = BinaryContractSerializer.Deserialize<TestRoot>(encoded.Span);

        TestRoots.AssertEquivalent(value, decoded);
        Assert.Null(decoded.Text);
    }

    [Fact]
    public void ReaderRejectsNonCanonicalBoolean()
    {
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(TestRoots.Canonical(), 256);
        encoded[0] = 2;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BinaryContractSerializer.Deserialize<TestRoot>(encoded.Span));

        Assert.Contains("Invalid Boolean value 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderRejectsMalformedUtf8()
    {
        byte[] malformed = [2, 0, 0, 0, 0xC3, 0x28];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ReadOneString(malformed));

        Assert.Contains("malformed UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractDeserializerRejectsEveryTruncatedPrefix()
    {
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(TestRoots.Canonical(), 256);

        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<InvalidDataException>(
                () => BinaryContractSerializer.Deserialize<TestRoot>(encoded.Span[..length]));
        }
    }

    [Fact]
    public void ContractDeserializerRejectsTrailingBytes()
    {
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(TestRoots.Canonical(), 256);
        int originalLength = encoded.Length;
        encoded.SetLength(originalLength + 1);
        encoded[originalLength] = 0;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BinaryContractSerializer.Deserialize<TestRoot>(encoded.Span));

        Assert.Contains("unexpected trailing bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredReadLimitsRejectDepthStringPayloadAndAllocationExcesses()
    {
        OwnedTestEncoding encoded = OwnedTestEncoding.Encode(
            TestRoots.Canonical(text: "limit", data: [1, 2, 3, 4]),
            256);

        Assert.Throws<InvalidDataException>(() => BinaryContractSerializer.Deserialize<TestRoot>(
            encoded.Span,
            new BinaryReadLimits { MaxObjectDepth = 0 }));
        Assert.Throws<InvalidDataException>(() => BinaryContractSerializer.Deserialize<TestRoot>(
            encoded.Span,
            new BinaryReadLimits { MaxStringBytes = 4 }));
        Assert.Throws<InvalidDataException>(() => BinaryContractSerializer.Deserialize<TestRoot>(
            encoded.Span,
            new BinaryReadLimits { MaxTotalStringBytes = 4 }));
        Assert.Throws<InvalidDataException>(() => BinaryContractSerializer.Deserialize<TestRoot>(
            encoded.Span,
            new BinaryReadLimits { MaxBytePayloadBytes = 3 }));
        Assert.Throws<InvalidDataException>(() => BinaryContractSerializer.Deserialize<TestRoot>(
            encoded.Span,
            new BinaryReadLimits { MaxAllocationBytes = 32 }));
    }

    private static string? ReadOneString(byte[] encoded)
    {
        var reader = new BinaryDataReader(encoded);
        return reader.ReadString();
    }
}
