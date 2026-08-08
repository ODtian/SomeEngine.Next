namespace SomeEngine.Serialization.IO;

/// <summary>
/// Synchronous forward-only stream that writes directly to one destination, counts accepted bytes,
/// and rejects an over-limit append before touching the destination. It retains no payload bytes.
/// </summary>
public sealed class BoundedCountingWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly Action<long, int>? _validateAppend;
    private readonly Func<long, int, long, Exception>? _limitExceeded;
    private bool _disposed;

    public BoundedCountingWriteStream(
        Stream destination,
        long maximumBytes,
        bool leaveOpen = true,
        Action<long, int>? validateAppend = null,
        Func<long, int, long, Exception>? limitExceeded = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Counting destination must be writable.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        _destination = destination;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _validateAppend = validateAppend;
        _limitExceeded = limitExceeded;
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => BytesWritten;
    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _destination.Flush();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("Offset and count exceed the supplied buffer.");
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length > _maximumBytes - BytesWritten)
            ThrowLimitExceeded(buffer.Length);
        _validateAppend?.Invoke(BytesWritten, buffer.Length);
        _destination.Write(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1] { value };
        Write(one);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("BoundedCountingWriteStream is synchronous.");

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("BoundedCountingWriteStream is synchronous.");

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("BoundedCountingWriteStream is synchronous.");

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
                _destination.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ThrowLimitExceeded(int attemptedBytes)
    {
        Exception exception = _limitExceeded?.Invoke(
            BytesWritten,
            attemptedBytes,
            _maximumBytes) ?? new InvalidDataException(
                $"Counted output exceeds the configured {_maximumBytes}-byte limit.");
        throw exception;
    }
}

/// <summary>
/// Synchronous forward-only stream that counts bytes read from one source and rejects another read
/// after a configured limit. It is used when a footer declares the exact length only after a codec
/// has consumed its payload, so no payload frame needs to be retained.
/// </summary>
public sealed class BoundedCountingReadStream : Stream
{
    private readonly Stream _source;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly Func<long, int, long, Exception>? _limitExceeded;
    private bool _disposed;

    public BoundedCountingReadStream(
        Stream source,
        long maximumBytes,
        bool leaveOpen = true,
        Func<long, int, long, Exception>? limitExceeded = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Counting source must be readable.", nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        _source = source;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _limitExceeded = limitExceeded;
    }

    public long BytesRead { get; private set; }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("Offset and count exceed the supplied buffer.");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
            return 0;

        long remaining = _maximumBytes - BytesRead;
        if (remaining == 0)
            ThrowLimitExceeded(buffer.Length);
        int requested = (int)Math.Min(buffer.Length, remaining);
        int read = _source.Read(buffer[..requested]);
        BytesRead = checked(BytesRead + read);
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("BoundedCountingReadStream is synchronous.");

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("BoundedCountingReadStream is synchronous.");

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
                _source.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ThrowLimitExceeded(int attemptedBytes)
    {
        Exception exception = _limitExceeded?.Invoke(
            BytesRead,
            attemptedBytes,
            _maximumBytes) ?? new InvalidDataException(
                $"Counted input exceeds the configured {_maximumBytes}-byte limit.");
        throw exception;
    }
}
