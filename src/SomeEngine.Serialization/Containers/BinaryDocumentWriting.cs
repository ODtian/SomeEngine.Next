using System.Buffers;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Containers;

internal interface IBinaryDocumentRoot
{
    void WriteTo(IStreamingBinarySink destination);
}

internal sealed class ContractDocumentRoot<T>(T value) : IBinaryDocumentRoot
    where T : IBinaryContract<T>
{
    public void WriteTo(IStreamingBinarySink destination)
        => BinaryContractSerializer.Write(destination, value);
}

internal sealed class HashingBufferWriter : IStreamingBinarySink, IDisposable
{
    private readonly HashingWriteStream _destination;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private int _buffered;
    private bool _completed;

    internal HashingBufferWriter(Stream destination) =>
        _destination = new HashingWriteStream(destination);

    internal long WrittenCount { get; private set; }

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _buffered)
            throw new ArgumentOutOfRangeException(nameof(count));
        _buffered += count;
        WrittenCount = checked(WrittenCount + count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureBuffer(sizeHint);
        return _buffer!.AsMemory(_buffered);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureBuffer(sizeHint);
        return _buffer.AsSpan(_buffered);
    }

    public void WriteDirect(ReadOnlySpan<byte> value)
    {
        EnsureActive();
        FlushBuffered();
        if (value.IsEmpty)
            return;
        _destination.Write(value);
        WrittenCount = checked(WrittenCount + value.Length);
    }

    public void WriteZeroesDirect(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureActive();
        FlushBuffered();
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        while (count > 0)
        {
            int length = Math.Min(count, zeroes.Length);
            _destination.Write(zeroes[..length]);
            WrittenCount = checked(WrittenCount + length);
            count -= length;
        }
    }

    internal Digest256 CompleteDigest()
    {
        EnsureActive();
        FlushBuffered();
        _completed = true;
        return _destination.CompleteDigest();
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
        _destination.Dispose();
    }

    private void EnsureBuffer(int sizeHint)
    {
        EnsureActive();
        if (sizeHint <= 0)
            sizeHint = 1;
        if (sizeHint <= _buffer!.Length - _buffered)
            return;

        FlushBuffered();
        if (sizeHint <= _buffer.Length)
            return;

        byte[] old = _buffer;
        _buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(old);
    }

    private void FlushBuffered()
    {
        if (_buffered == 0)
            return;
        ReadOnlySpan<byte> bytes = _buffer.AsSpan(0, _buffered);
        _destination.Write(bytes);
        _buffered = 0;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (_completed)
            throw new InvalidOperationException("The root hash has already been completed.");
    }
}
