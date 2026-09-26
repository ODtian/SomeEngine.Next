using System.Buffers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class ObjectDestinationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeIntoCompletesNewCollectionsBeforeCallingACopyingSetter(bool readOnly)
    {
        var source = new CopyingCollectionValue { Values = [3, 8, 13] };
        var output = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(output, source);
        var target = new CopyingCollectionValue(readOnly);
        BinarySerializer.DeserializeInto(output.WrittenSpan, target);
        Assert.Equal(source.Values, target.Values);
        IList<int> retained = target.Values!;
        BinarySerializer.DeserializeInto(new ReadOnlySequence<byte>(output.WrittenMemory), target);
        Assert.Same(retained, target.Values);
        Assert.Equal(source.Values, target.Values);
    }

    [Fact]
    public void DecodeIntoReusesTheObjectAndItsSelectedPageCollection()
    {
        var source = new ObjectDestinationValue(Guid.NewGuid(),
            Enumerable.Range(0, 3000).Select(i => new GeneratedPoint { X = i, Y = -i }).ToArray());
        using var arena = new PageArena(new ResidencyBudgetLedger());
        using var encoded = new PageBufferWriter(arena);
        BinarySerializer.Write(encoded, source);
        using var input = encoded.Retain();
        using var points = new PageList<GeneratedPoint>(arena);
        points.Add(new GeneratedPoint { X = 100, Y = 200 });
        var target = new ObjectDestinationValue(Guid.NewGuid(), points);
        int constructed = ObjectDestinationValue.ConstructedCount;
        BinarySerializer.DeserializeInto(input.Sequence, target);
        Assert.Equal(source.Id, target.Id);
        Assert.Same(points, target.Points);
        Assert.Equal(source.Points, points);
        Assert.Equal(constructed, ObjectDestinationValue.ConstructedCount);
    }

    [Fact]
    public async Task DocumentRootDecodesIntoTheTargetWithoutAnotherSourceRead()
    {
        var source = new ObjectDestinationValue(Guid.NewGuid(), [new() { X = 1.5f, Y = -7 }]);
        using var file = BinaryDocumentWriter.Create(source).BuildMapped();
        var ranges = new ClosingReads(new MemoryRangeSource(file));
        await using var document = await BinaryDocument.OpenAsync(ranges,
            new BinaryWireTypeDescriptor(ObjectDestinationValue.TypeId, ObjectDestinationValue.SchemaFingerprint,
                ObjectDestinationValue.Compatibility, ObjectDestinationValue.SchemaEpoch), ownsSource: true);
        ranges.ReadsClosed = true;
        var points = new List<GeneratedPoint>();
        var target = new ObjectDestinationValue(Guid.Empty, points);
        document.ReadRootInto(target);
        Assert.Same(points, target.Points);
        Assert.Equal(source.Id, target.Id);
        Assert.Equal(source.Points, target.Points);
    }

    [Fact]
    public void DecodeIntoAppliesReadLimitsAndHandlesAReadOnlyCollectionTarget()
    {
        var source = new ObjectDestinationValue(Guid.NewGuid(), [new() { X = 4, Y = 5 }]);
        var encoded = new ArrayBufferWriter<byte>();
        BinarySerializer.Write(encoded, source);
        var target = new ObjectDestinationValue(Guid.Empty, Array.Empty<GeneratedPoint>());
        BinarySerializer.DeserializeInto(encoded.WrittenSpan, target);
        Assert.False(target.Points.IsReadOnly);
        Assert.Equal(source.Points, target.Points);
        Assert.Throws<InvalidDataException>(() => BinarySerializer.DeserializeInto(
            encoded.WrittenSpan, target, new BinaryReadLimits { MaxCollectionCount = 0 }));
    }

    private sealed class ClosingReads(IRangeSource source) : IRangeSource
    {
        internal bool ReadsClosed;
        public long Length => source.Length;
        public string Generation => source.Generation;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
            => ReadsClosed ? throw new IOException("No more reads allowed.") : source.ReadExactlyAsync(offset, destination, cancellationToken);
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => ReadsClosed ? throw new IOException("No more reads allowed.") : source.AcquireAsync(offset, length, cancellationToken);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}

public abstract class ObjectDestinationIdentity
{
    public Guid Id { get; protected set; }
}

[BinarySerializable]
public partial class ObjectDestinationValue : ObjectDestinationIdentity
{
    public static int ConstructedCount;
    public IList<GeneratedPoint> Points { get; set; } = new List<GeneratedPoint>();
    public ObjectDestinationValue() => ConstructedCount++;
    public ObjectDestinationValue(Guid id, IList<GeneratedPoint> points) : this() { Id = id; Points = points; }
}

[BinarySerializable]
public partial class CopyingCollectionValue
{
    private IList<int>? _values;
    public CopyingCollectionValue() { }
    public CopyingCollectionValue(bool readOnly) => _values = readOnly ? Array.Empty<int>() : null;
    public IList<int>? Values
    {
        get => _values;
        set => _values = value?.ToList();
    }
}
