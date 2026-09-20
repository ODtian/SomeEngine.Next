using System.Buffers;

namespace SomeEngine.Serialization.IO;

/// <summary>A buffered stream view over explicit range reads. The caller retains the source.</summary>
public sealed class RangeReadStream : Stream
{
    private readonly IRangeSource _source;
    private readonly CancellationToken _cancellationToken;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private long _position;
    private long _bufferOffset = -1;
    private int _bufferLength;

    public RangeReadStream(IRangeSource source, CancellationToken cancellationToken = default)
    {
        _source = source;
        _cancellationToken = cancellationToken;
    }
    public override bool CanRead => _buffer is not null;
    public override bool CanSeek => _buffer is not null;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        _cancellationToken.ThrowIfCancellationRequested();
        int total = 0;
        while (!destination.IsEmpty && _position < Length)
        {
            if (_position < _bufferOffset || _position >= _bufferOffset + _bufferLength)
            {
                _bufferOffset = -1;
                _bufferLength = 0;
                int length = (int)Math.Min(_buffer.Length, Length - _position);
                _source.ReadExactlyAsync(_position, _buffer.AsMemory(0, length), _cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                _bufferOffset = _position;
                _bufferLength = length;
            }
            int offset = checked((int)(_position - _bufferOffset));
            int count = Math.Min(destination.Length, _bufferLength - offset);
            _buffer.AsSpan(offset, count).CopyTo(destination);
            destination = destination[count..];
            _position += count;
            total += count;
        }
        return total;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (position < 0) throw new IOException("Cannot seek before the source.");
        return _position = position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        base.Dispose(disposing);
    }
}
