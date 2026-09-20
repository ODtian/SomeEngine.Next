using SomeEngine.Serialization.Containers;

namespace SomeEngine.Serialization.IO;

/// <summary>
/// A seekable stream over caller-retained pages. Length describes committed bytes; the supplied
/// buffer can hold additional capacity. Disposing the stream leaves the buffer with its caller.
/// </summary>
public sealed class PageBufferStream : Stream
{
    private readonly PageBuffer _buffer;
    private readonly bool _writable;
    private long _length;
    private long _position;
    private bool _disposed;

    public PageBufferStream(PageBuffer buffer, bool writable = true)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _length = buffer.Length;
        _writable = writable;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed && _writable;
    public override long Length { get { RequireOpen(); return _length; } }
    public override long Position
    {
        get { RequireOpen(); return _position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }
    public override int Read(Span<byte> destination)
    {
        RequireOpen();
        if (_position >= _length) return 0;
        int count = (int)Math.Min(destination.Length, _length - _position);
        _buffer.Read(_position, destination[..count]);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value)
    {
        ReadOnlySpan<byte> bytes = [value];
        Write(bytes);
    }
    public override void Write(ReadOnlySpan<byte> source)
    {
        RequireWritable();
        long end = checked(_position + source.Length);
        Grow(end);
        if (_position > _length)
            _buffer.Write(_length, _position - _length, static (_, bytes) => bytes.Clear());
        _buffer.Write(_position, source);
        _position = end;
        _length = Math.Max(_length, end);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        RequireOpen();
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (position < 0) throw new IOException("Cannot seek before the stream.");
        return _position = position;
    }

    public override void SetLength(long value)
    {
        RequireWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value < _length) _buffer.Resize(value);
        else
        {
            Grow(value);
            _buffer.Write(_length, value - _length, static (_, bytes) => bytes.Clear());
        }
        _length = value;
        if (_position > value) _position = value;
    }

    private void Grow(long required)
    {
        long capacity = _buffer.Length;
        if (required <= capacity) return;
        long growth = Math.Max(required - capacity,
            Math.Max(_buffer.Arena.SizeFor(1), Math.Min(capacity, _buffer.Arena.MaximumPageSize)));
        _buffer.Resize(checked(capacity + growth));
    }

    public override void Flush() => RequireOpen();
    private void RequireOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void RequireWritable()
    {
        RequireOpen();
        if (!_writable) throw new NotSupportedException("The page stream is read-only.");
    }
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
