using System.Buffers;

namespace SomeEngine.Serialization.Tests;

public sealed class StandardBufferTests
{
    [Fact]
    public void EverySplitIncludingUtf8AndIntegerBoundariesMatchesContinuousInput()
    {
        TestRoot expected = TestRoots.Canonical(text: "Aé水😀𐐷", data: [1, 2, 3, 4, 5]);
        var output = new ArrayBufferWriter<byte>();
        int count = BinarySerializer.Write(output, expected);
        Assert.Equal(count, output.WrittenCount);
        for (int split = 0; split <= count; split++)
        {
            ReadOnlySequence<byte> sequence = Split(output.WrittenMemory, split);
            TestRoot actual = BinarySerializer.Deserialize<TestRoot>(sequence);
            var encoded = new ArrayBufferWriter<byte>();
            BinarySerializer.Write(encoded, actual);
            Assert.True(output.WrittenSpan.SequenceEqual(encoded.WrittenSpan));
        }

        ReadOnlySequence<byte> singleBytes = Segment(output.WrittenMemory, 1);
        TestRoot bytewise = BinarySerializer.Deserialize<TestRoot>(singleBytes);
        Assert.Equal(expected.Text, bytewise.Text);
        Assert.Equal(expected.Data, bytewise.Data);
    }

    [Fact]
    public void LargeOwnedFieldAllocatesOnlyItsFinalDecodedBuffer()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        new Random(37).NextBytes(payload);
        var output = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(output, TestRoots.Canonical(data: payload));
        ReadOnlySequence<byte> input = Segment(output.WrittenMemory, 4096);
        _ = BinarySerializer.Deserialize<TestRoot>(input);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TestRoot actual = BinarySerializer.Deserialize<TestRoot>(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(payload, actual.Data);
        Assert.InRange(allocated, payload.Length, payload.Length + 64 * 1024);
    }

    [Fact]
    public void BoundedOutputReceivesOneEncodingWithoutAWholeValueBuffer()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        new Random(71).NextBytes(payload);
        TestRoot value = TestRoots.Canonical(text: "é😀", data: payload);
        var expected = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(expected, value);
        var output = new WindowWriter(expected.WrittenMemory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int written = BinarySerializer.Write(output, value);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected.WrittenCount, written);
        Assert.Equal(written, output.WrittenCount);
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Fact]
    public void ByteFieldAndAggregateStringLimitsApplyToBothInputForms()
    {
        var output = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(output, TestRoots.Canonical(data: new byte[32]));
        ReadOnlySequence<byte> sequence = Segment(output.WrittenMemory, 1);
        var limits = new BinaryReadLimits { MaxBytePayloadBytes = 8 };
        Assert.Throws<InvalidDataException>(() => BinarySerializer.Deserialize<TestRoot>(output.WrittenSpan, limits));
        Assert.Throws<InvalidDataException>(() => BinarySerializer.Deserialize<TestRoot>(sequence, limits));

        output.Clear();
        var writer = new BinaryDataWriter(output);
        writer.WriteString("hello");
        int firstLength = writer.WrittenCount;
        writer.WriteString("world");
        sequence = Segment(output.WrittenMemory, 1);
        limits = new BinaryReadLimits { MaxTotalStringBytes = 9 };
        Assert.Throws<InvalidDataException>(() => ReadTwoChildren(sequence, firstLength, limits));
    }

    [Fact]
    public void SplitGuidReadsDoNotAllocatePerValue()
    {
        const int count = 1000;
        Guid expected = new("37c5df14-0174-46ea-a2b3-ef7880100288");
        var output = new ArrayBufferWriter<byte>();
        var writer = new BinaryDataWriter(output);
        for (int i = 0; i < count; i++) writer.WriteGuid(expected);
        ReadOnlySequence<byte> sequence = Segment(output.WrittenMemory, 7);
        var reader = new BinaryDataReader(sequence);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool equal = true;
        for (int i = 0; i < count; i++) equal &= reader.ReadGuid() == expected;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(equal);
        Assert.True(reader.End);
        Assert.Equal(0, allocated);
    }

    private static void ReadTwoChildren(ReadOnlySequence<byte> sequence, int firstLength, BinaryReadLimits limits)
    {
        var reader = new BinaryDataReader(sequence, limits);
        BinaryDataReader first = reader.ReadSubReader(firstLength);
        Assert.Equal("hello", first.ReadString());
        BinaryDataReader second = reader.ReadSubReader(reader.Remaining);
        _ = second.ReadString();
    }

    private static ReadOnlySequence<byte> Split(ReadOnlyMemory<byte> memory, int split)
    {
        var first = new SegmentBytes(memory[..split]);
        SegmentBytes last = first.Append(memory[split..]);
        return new(first, 0, last, last.Memory.Length);
    }

    private static ReadOnlySequence<byte> Segment(ReadOnlyMemory<byte> memory, int size)
    {
        var first = new SegmentBytes(memory[..Math.Min(size, memory.Length)]);
        SegmentBytes last = first;
        for (int offset = first.Memory.Length; offset < memory.Length; offset += size)
            last = last.Append(memory.Slice(offset, Math.Min(size, memory.Length - offset)));
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class SegmentBytes : ReadOnlySequenceSegment<byte>
    {
        internal SegmentBytes(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal SegmentBytes Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SegmentBytes(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class WindowWriter(ReadOnlyMemory<byte> expected) : IBufferWriter<byte>
    {
        private readonly byte[] _window = new byte[64 * 1024];
        internal int WrittenCount { get; private set; }
        public void Advance(int count)
        {
            Assert.True(expected.Span.Slice(WrittenCount, count).SequenceEqual(_window.AsSpan(0, count)));
            WrittenCount += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _window.Length) throw new InvalidOperationException("The output accepts only a bounded write window.");
            return _window;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
