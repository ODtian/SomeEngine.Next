using System.Security.Cryptography;

namespace SomeEngine.Serialization.IO;

/// <summary>
/// Synchronous bounded stream that reads directly from one source while updating a caller-selected
/// 256-bit incremental digest. It owns no payload backing. A caller may authenticate fixed metadata
/// after the payload without introducing a second domain-specific hashing stream.
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _source;
    private readonly IncrementalHash _hasher;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly bool _leaveHasherOpen;
    private bool _completed;
    private bool _disposed;

    public HashingReadStream(
        Stream source,
        long length,
        bool leaveOpen = true)
    {
        ValidateArguments(source, length);

        _source = source;
        _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _length = length;
        _leaveOpen = leaveOpen;
        _leaveHasherOpen = false;
    }

    public HashingReadStream(
        Stream source,
        long length,
        IncrementalHash hasher,
        bool leaveOpen = true,
        bool leaveHasherOpen = false)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ValidateArguments(source, length);

        _source = source;
        _hasher = hasher;
        _length = length;
        _leaveOpen = leaveOpen;
        _leaveHasherOpen = leaveHasherOpen;
    }

    public long BytesRead { get; private set; }
    public long Remaining => _length - BytesRead;

    public override bool CanRead => !_disposed && !_completed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public Digest256 CompleteDigest(ReadOnlySpan<byte> authenticatedSuffix = default)
    {
        EnsureActive();
        if (Remaining != 0)
            throw new InvalidDataException("Hashed input has not been consumed completely.");

        if (!authenticatedSuffix.IsEmpty)
            _hasher.AppendData(authenticatedSuffix);
        _completed = true;
        return Digest256.Finish(_hasher);
    }

    public Digest256 DrainAndCompleteDigest(ReadOnlySpan<byte> authenticatedSuffix = default)
    {
        EnsureActive();
        Span<byte> buffer = stackalloc byte[4096];
        while (Remaining != 0)
            _ = Read(buffer);
        return CompleteDigest(authenticatedSuffix);
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
        EnsureActive();
        if (Remaining == 0 || buffer.IsEmpty)
            return 0;

        int requested = (int)Math.Min(Remaining, buffer.Length);
        int read = _source.Read(buffer[..requested]);
        if (read == 0)
            throw new EndOfStreamException("The hashed input ended before its declared length.");

        _hasher.AppendData(buffer[..read]);
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
        throw new NotSupportedException("HashingReadStream is synchronous.");

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("HashingReadStream is synchronous.");

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
            if (disposing)
            {
                if (!_leaveHasherOpen)
                    _hasher.Dispose();
                if (!_leaveOpen)
                    _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The input digest has already been completed.");
    }

    private static void ValidateArguments(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Hashing source must be readable.", nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
    }
}
