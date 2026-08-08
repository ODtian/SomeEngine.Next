using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace SomeEngine.Serialization.IO;

/// <summary>A stable-generation source that supports explicit-offset reads.</summary>
public interface IRangeSource : IAsyncDisposable
{
    long Length { get; }

    /// <summary>Immutable identity captured when the source is opened.</summary>
    string Generation { get; }

    /// <summary>Whether acquired ranges remain immutable for the full lease lifetime.</summary>
    bool LeasesAreImmutable => false;

    /// <summary>
    /// Whether this source itself keeps the complete addressed bytes resident in memory (including
    /// a memory mapping). The default is deliberately conservative so wrappers must explicitly
    /// propagate the capability instead of silently enabling a second final payload backing.
    /// </summary>
    bool RetainsResidentBacking => true;

    ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);

    ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional external authentication receipt carried by a range source. BinaryDocument validates
/// the receipt after its own header, catalog, root, and directory validation, so storage backends
/// never need a second document parser.
/// </summary>
internal interface IBinaryDocumentReceipt
{
    void Validate(Digest256 documentDigest);
}

public sealed class RangeLease : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly ReadOnlyMemory<byte> _memory;
    private int _disposed;

    private RangeLease(ReadOnlyMemory<byte> memory, IMemoryOwner<byte>? owner)
    {
        _memory = memory;
        _owner = owner;
    }

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _memory;
        }
    }

    public int Length => _memory.Length;

    public static RangeLease Borrow(ReadOnlyMemory<byte> memory) => new(memory, owner: null);

    public static RangeLease Own(IMemoryOwner<byte> owner, int length)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        return new RangeLease(owner.Memory[..length], owner);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}

public sealed class MemoryRangeSource : IRangeSource
{
    private readonly ReadOnlyMemory<byte> _memory;
    private IMemoryOwner<byte>? _owner;
    private int _disposed;

    /// <summary>
    /// Borrows immutable memory. The caller must keep it alive and unmodified until this source is disposed.
    /// </summary>
    public MemoryRangeSource(ReadOnlyMemory<byte> memory, string? generation = null)
    {
        _memory = memory;
        Generation = generation ?? $"memory:{Guid.NewGuid():N}";
    }

    /// <summary>Transfers ownership of one immutable memory backing store to this source.</summary>
    public MemoryRangeSource(IMemoryOwner<byte> owner, int length, string? generation = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _owner = owner;
        _memory = owner.Memory[..length];
        Generation = generation ?? $"memory:{Guid.NewGuid():N}";
    }

    public long Length => _memory.Length;
    public string Generation { get; }
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => true;

    public ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, destination.Length, Length);
        _memory.Slice(checked((int)offset), destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, length, Length);
        return ValueTask.FromResult(RangeLease.Borrow(_memory.Slice(checked((int)offset), length)));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Interlocked.Exchange(ref _owner, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class FileRangeSource : IRangeSource
{
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private FileRangeSource(SafeFileHandle handle, long length, string generation)
    {
        _handle = handle;
        Length = length;
        Generation = generation;
    }

    public long Length { get; }
    public string Generation { get; }
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => false;

    public static FileRangeSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            long length = RandomAccess.GetLength(handle);
            // The identity belongs to this opened handle, not to mutable path metadata. In
            // particular, replacing the path while this handle is alive must not make this
            // source appear to have switched generations.
            string generation = $"file:{fullPath}:{length:X16}:{Guid.NewGuid():N}";
            return new FileRangeSource(handle, length, generation);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, destination.Length, Length);

        int total = 0;
        while (total < destination.Length)
        {
            int read = await RandomAccess.ReadAsync(
                _handle,
                destination[total..],
                checked(offset + total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Range source was truncated while reading at offset {offset + total}.");
            total += read;
        }
    }

    public async ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, length, Length);
        if (length == 0)
            return RangeLease.Borrow(ReadOnlyMemory<byte>.Empty);

        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            await ReadExactlyAsync(offset, owner.Memory[..length], cancellationToken).ConfigureAwait(false);
            return RangeLease.Own(owner, length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal static class RangeValidation
{
    internal static void Validate(long offset, long length, long sourceLength)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(length), exception);
        }

        if (end > sourceLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Range [{offset}, {end}) exceeds source length {sourceLength}.");
        }
    }
}
