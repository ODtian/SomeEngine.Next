using System.Collections.Concurrent;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

internal sealed record TestRoot(
    bool Enabled,
    byte ByteValue,
    sbyte SignedByteValue,
    short Int16Value,
    ushort UInt16Value,
    int Int32Value,
    uint UInt32Value,
    long Int64Value,
    ulong UInt64Value,
    float SingleValue,
    double DoubleValue,
    char Character,
    Guid Id,
    string? Text,
    byte[] Data) : IBinaryContract<TestRoot>
{
    internal const ulong Fingerprint = 0x9E21A4FC19D7B403UL;
    internal static readonly Guid StableTypeId = Guid.Parse("7b1dbb5d-6f37-4a13-9da0-30cb7642d66d");

    public static Guid TypeId => StableTypeId;
    public static ulong SchemaFingerprint => Fingerprint;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 7;

    public static void Write(ref BinaryDataWriter writer, TestRoot value)
    {
        writer.WriteBoolean(value.Enabled);
        writer.WriteByte(value.ByteValue);
        writer.WriteSByte(value.SignedByteValue);
        writer.WriteInt16(value.Int16Value);
        writer.WriteUInt16(value.UInt16Value);
        writer.WriteInt32(value.Int32Value);
        writer.WriteUInt32(value.UInt32Value);
        writer.WriteInt64(value.Int64Value);
        writer.WriteUInt64(value.UInt64Value);
        writer.WriteSingle(value.SingleValue);
        writer.WriteDouble(value.DoubleValue);
        writer.WriteChar(value.Character);
        writer.WriteGuid(value.Id);
        writer.WriteString(value.Text);
        writer.WriteLengthPrefixedBytes(value.Data);
    }

    public static TestRoot Read(ref BinaryDataReader reader)
    {
        reader.EnterObject();
        try
        {
            return new TestRoot(
                reader.ReadBoolean(),
                reader.ReadByte(),
                reader.ReadSByte(),
                reader.ReadInt16(),
                reader.ReadUInt16(),
                reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadInt64(),
                reader.ReadUInt64(),
                reader.ReadSingle(),
                reader.ReadDouble(),
                reader.ReadChar(),
                reader.ReadGuid(),
                reader.ReadString(),
                reader.ReadByteArray());
        }
        finally
        {
            reader.ExitObject();
        }
    }
}

internal static class TestRoots
{
    internal static readonly Guid CanonicalId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    internal static TestRoot Canonical(string? text = "Aé水", byte[]? data = null, int number = 0x12345678)
        => new(
            Enabled: true,
            ByteValue: 0xAB,
            SignedByteValue: -2,
            Int16Value: 0x1234,
            UInt16Value: 0xABCD,
            Int32Value: number,
            UInt32Value: 0x90ABCDEF,
            Int64Value: 0x0102030405060708,
            UInt64Value: 0x8899AABBCCDDEEFF,
            SingleValue: 1.0f,
            DoubleValue: -2.5,
            Character: '水',
            Id: CanonicalId,
            Text: text,
            Data: data ?? [1, 2, 3]);

    internal static void AssertEquivalent(TestRoot expected, TestRoot actual)
    {
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.ByteValue, actual.ByteValue);
        Assert.Equal(expected.SignedByteValue, actual.SignedByteValue);
        Assert.Equal(expected.Int16Value, actual.Int16Value);
        Assert.Equal(expected.UInt16Value, actual.UInt16Value);
        Assert.Equal(expected.Int32Value, actual.Int32Value);
        Assert.Equal(expected.UInt32Value, actual.UInt32Value);
        Assert.Equal(expected.Int64Value, actual.Int64Value);
        Assert.Equal(expected.UInt64Value, actual.UInt64Value);
        Assert.Equal(expected.SingleValue, actual.SingleValue);
        Assert.Equal(expected.DoubleValue, actual.DoubleValue);
        Assert.Equal(expected.Character, actual.Character);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Data, actual.Data);
    }
}

internal readonly record struct RangeOperation(long Offset, int Length);

internal sealed class CountingRangeSource : IRangeSource
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly ConcurrentQueue<RangeOperation> _operations = new();
    private string _generation;
    private int _disposed;

    internal CountingRangeSource(byte[] bytes, string generation = "generation:1")
    {
        _bytes = bytes;
        _generation = generation;
    }

    internal CountingRangeSource(MappedTestDocument bytes, string generation = "generation:1")
    {
        _bytes = bytes;
        _generation = generation;
    }

    public long Length => _bytes.Length;
    public string Generation => Volatile.Read(ref _generation);
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => true;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal RangeOperation[] Operations => _operations.ToArray();

    internal void ResetOperations()
    {
        while (_operations.TryDequeue(out _))
        {
        }
    }

    internal void AdvanceGeneration()
        => Interlocked.Exchange(ref _generation, $"generation:{Guid.NewGuid():N}");

    public ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(offset, destination.Length);
        _operations.Enqueue(new RangeOperation(offset, destination.Length));
        _bytes.Slice(checked((int)offset), destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(offset, length);
        _operations.Enqueue(new RangeOperation(offset, length));
        return ValueTask.FromResult(RangeLease.Borrow(_bytes.Slice(checked((int)offset), length)));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void Validate(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class SparseRangeSource : IRangeSource
{
    private readonly ReadOnlyMemory<byte> _metadata;
    private readonly long _payloadOffset;
    private readonly ReadOnlyMemory<byte> _payload;
    private readonly ConcurrentQueue<RangeOperation> _operations = new();
    private int _disposed;

    internal SparseRangeSource(
        long length,
        ReadOnlyMemory<byte> metadata,
        long payloadOffset,
        ReadOnlyMemory<byte> payload)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (payloadOffset < 0 || payloadOffset > length - payload.Length)
            throw new ArgumentOutOfRangeException(nameof(payloadOffset));
        if (metadata.Length > length)
            throw new ArgumentOutOfRangeException(nameof(metadata));

        Length = length;
        _metadata = metadata;
        _payloadOffset = payloadOffset;
        _payload = payload;
    }

    public long Length { get; }
    public string Generation => "sparse:generation:1";
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => false;
    internal RangeOperation[] Operations => _operations.ToArray();

    internal void ResetOperations()
    {
        while (_operations.TryDequeue(out _))
        {
        }
    }

    public ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(offset, destination.Length);
        _operations.Enqueue(new RangeOperation(offset, destination.Length));
        Fill(offset, destination.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(offset, length);
        _operations.Enqueue(new RangeOperation(offset, length));
        var buffer = new byte[length];
        Fill(offset, buffer);
        return ValueTask.FromResult(RangeLease.Borrow(buffer));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void Fill(long offset, Span<byte> destination)
    {
        destination.Clear();
        CopyIntersection(offset, destination, 0, _metadata.Span);
        CopyIntersection(offset, destination, _payloadOffset, _payload.Span);
    }

    private static void CopyIntersection(
        long requestOffset,
        Span<byte> destination,
        long segmentOffset,
        ReadOnlySpan<byte> segment)
    {
        long start = Math.Max(requestOffset, segmentOffset);
        long end = Math.Min(requestOffset + destination.Length, segmentOffset + segment.Length);
        if (start >= end)
            return;

        int length = checked((int)(end - start));
        segment.Slice(checked((int)(start - segmentOffset)), length)
            .CopyTo(destination.Slice(checked((int)(start - requestOffset)), length));
    }

    private void Validate(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
