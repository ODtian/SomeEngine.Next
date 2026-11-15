using System.Buffers;
using System.Collections.Immutable;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class ImmutableArrayTests
{
    [Fact]
    public void ImmutableArraysPreserveNullEmptyAndValuesThroughBothReaders()
    {
        foreach (ImmutableArray<byte>? optional in new ImmutableArray<byte>?[] { null, ImmutableArray<byte>.Empty, ImmutableArray.Create<byte>(3, 7) })
        {
            var expected = new ImmutableArrayValue { Bytes = ImmutableArray.Create<byte>(1, 2, 3), Optional = optional };
            var output = new ArrayBufferWriter<byte>();
            BinarySerializer.Write(output, expected);
            var decoded = BinarySerializer.Deserialize<ImmutableArrayValue>(output.WrittenSpan);
            Assert.Equal<byte>(expected.Bytes, decoded.Bytes);
            Assert.Equal(expected.Optional.HasValue, decoded.Optional.HasValue);
            if (optional.HasValue) Assert.Equal<byte>(optional.Value, decoded.Optional!.Value);
            var view = new ImmutableArrayValue.SpanView(output.WrittenSpan);
            Assert.False(view.GetBytesEncoded().IsEmpty);
            var target = new ImmutableArrayValue();
            BinarySerializer.DeserializeInto(new ReadOnlySequence<byte>(output.WrittenMemory), target);
            Assert.Equal<byte>(expected.Bytes, target.Bytes);
        }
    }

    [Fact]
    public void UninitializedArrayIsRejectedInsteadOfBecomingAnImplicitEmptyValue()
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.Throws<InvalidDataException>(() => BinarySerializer.Write(output, new ImmutableArrayValue { Bytes = default }));
    }
}

[BinarySerializable]
public partial class ImmutableArrayValue
{
    public ImmutableArray<byte> Bytes { get; set; } = ImmutableArray<byte>.Empty;
    public ImmutableArray<byte>? Optional { get; set; }
}
