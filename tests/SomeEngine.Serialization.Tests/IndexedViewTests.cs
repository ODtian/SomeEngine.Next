using System.Buffers;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class IndexedViewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16384)]
    public void RepeatedTailReadsDoNotRevalidateThePrefix(int count)
    {
        var output = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(output, new IndexedViewValue
        {
            Items = Enumerable.Range(0, count).Select(i => new MeasuredViewElement(i)).ToArray(),
            Text = "尾部文字",
            Value = 173,
        });
        MeasuredViewElement.ValidationCount = 0;
        var spanView = new IndexedViewValue.SpanView(output.WrittenSpan);
        using var memory = BinaryContractViewOwner.Borrow(output.WrittenMemory);
        var view = new IndexedViewValue.View(memory);
        Assert.Equal(count * 2, MeasuredViewElement.ValidationCount);
        Assert.Equal(173, spanView.GetValue());
        Assert.True(spanView.TryGetTextUtf8(out var text));
        Assert.Equal("尾部文字"u8.ToArray(), text.ToArray());

        // Initialization is measured separately from repeated field access.
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(173, view.GetValue());
            Assert.Equal(173, spanView.GetValue());
            Assert.True(view.TryGetTextUtf8(out _));
            Assert.True(spanView.TryGetTextUtf8(out _));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 1000; i++)
        {
            total += view.GetValue() + spanView.GetValue();
            view.TryGetTextUtf8(out var first);
            spanView.TryGetTextUtf8(out var second);
            total += first.Length + second.Length;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(370000, total);
        Assert.Equal(count * 2, MeasuredViewElement.ValidationCount);
        Assert.Equal(0, allocated);
        memory.Dispose();
        Assert.Throws<ObjectDisposedException>(() => view.GetValue());
    }

    [Fact]
    public void TruncatedPrefixOrTailCannotProduceAView()
    {
        var output = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(output, new IndexedViewValue
        {
            Items = [new(12), new(31)], Text = "text", Value = 7,
        });
        for (int length = 0; length < output.WrittenCount; length++)
        {
            using var memory = BinaryContractViewOwner.Borrow(output.WrittenMemory[..length]);
            Assert.Throws<InvalidDataException>(() => new IndexedViewValue.View(memory));
        }
    }
}

[BinarySerializable]
public partial class IndexedViewValue
{
    public MeasuredViewElement[] Items { get; set; } = [];
    public string Text { get; set; } = "";
    public int Value { get; set; }
}

[BinarySerializable]
public readonly record struct MeasuredViewElement(int Value)
    : IBinarySerializable<MeasuredViewElement>, IBinaryCustomViewContract<MeasuredViewElement>
{
    [ThreadStatic] public static int ValidationCount;
    public static Guid TypeId => new("14b3e124-8900-4cae-8c0b-cd1885d0e543");
    public static ulong SchemaFingerprint => 0x54243213;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public static void Write(ref BinaryDataWriter writer, MeasuredViewElement value) => writer.WriteInt32(value.Value);
    public static MeasuredViewElement Read(ref BinaryDataReader reader) => new(reader.ReadInt32());
    public static void ValidateView(ref BinaryViewReader reader)
    {
        _ = reader.ReadInt32();
        ValidationCount++;
    }
}
