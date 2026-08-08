using System.Security.Cryptography;

namespace SomeEngine.Serialization.IO;

/// <summary>
/// Synchronous forward-only stream that writes directly to a final destination while updating one
/// caller-selected 256-bit incremental digest. It never stages the payload or owns a second byte
/// backing. Asset documents, ECS checkpoints, and durable envelopes share this implementation.
/// </summary>
public sealed class HashingWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly IncrementalHash _hasher;
    private readonly long _maximumBytes;
    private readonly bool _leaveOpen;
    private readonly bool _leaveHasherOpen;
    private bool _completed;
    private bool _disposed;

    public HashingWriteStream(
        Stream destination,
        long maximumBytes = long.MaxValue,
        bool leaveOpen = true)
    {
        ValidateArguments(destination, maximumBytes);

        _destination = destination;
        _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _leaveHasherOpen = false;
    }

    public HashingWriteStream(
        Stream destination,
        IncrementalHash hasher,
        long maximumBytes = long.MaxValue,
        bool leaveOpen = true,
        bool leaveHasherOpen = false)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ValidateArguments(destination, maximumBytes);

        _destination = destination;
        _hasher = hasher;
        _maximumBytes = maximumBytes;
        _leaveOpen = leaveOpen;
        _leaveHasherOpen = leaveHasherOpen;
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed;
    public override long Length => BytesWritten;
    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public Digest256 CompleteDigest()
    {
        EnsureActive();
        _completed = true;
        return Digest256.Finish(_hasher);
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
        EnsureActive();
        if (buffer.Length > _maximumBytes - BytesWritten)
        {
            throw new InvalidDataException(
                $"Hashed output exceeds the configured {_maximumBytes}-byte limit.");
        }

        _destination.Write(buffer);
        _hasher.AppendData(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1] { value };
        Write(one);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "HashingWriteStream is synchronous so caller memory cannot change across an await boundary.");

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "HashingWriteStream is synchronous so caller memory cannot change across an await boundary.");

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "HashingWriteStream is synchronous so caller memory cannot change across an await boundary.");

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
            if (disposing)
            {
                if (!_leaveHasherOpen)
                    _hasher.Dispose();
                if (!_leaveOpen)
                    _destination.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The output digest has already been completed.");
    }

    private static void ValidateArguments(Stream destination, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Hashing destination must be writable.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
    }
}
