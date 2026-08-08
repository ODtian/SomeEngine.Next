using System.Buffers;
using System.IO.MemoryMappedFiles;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Serialization.Tests;

/// <summary>
/// Test-only writable view of the final document file. It never materializes the whole document
/// as a managed array; corruption tests patch the mapped final backing in place.
/// </summary>
internal sealed unsafe class MappedTestDocument : MemoryManager<byte>, IEquatable<MappedTestDocument>
{
    private readonly string _path;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _accessor;
    private byte* _pointer;
    private readonly int _length;
    private int _disposed;

    private MappedTestDocument(string path)
    {
        _path = path;
        var info = new FileInfo(path);
        if (info.Length > int.MaxValue)
            throw new InvalidOperationException("Mapped test documents must fit in contiguous Memory.");
        _length = checked((int)info.Length);
        _mapping = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite);
        _accessor = _mapping.CreateViewAccessor(0, _length, MemoryMappedFileAccess.ReadWrite);
        byte* basePointer = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
        _pointer = basePointer + _accessor.PointerOffset;
    }

    public int Length => _length;
    public long LongLength => _length;
    public byte this[int index]
    {
        get => GetSpan()[index];
        set => GetSpan()[index] = value;
    }

    internal string Path => _path;

    /// <summary>
    /// Releases the writable mapping and transfers the sole file backing to a nonresident range
    /// source. The returned source owns deletion of the temporary file.
    /// </summary>
    internal TestFileRangeSource DetachToFileRangeSource()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ReleaseMapping();
        Interlocked.Exchange(ref _disposed, 1);
        return TestFileRangeSource.OpenOwned(_path);
    }

    internal static MappedTestDocument Write(
        Action<FileStream> write)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"SomeEngine-test-document-{Guid.NewGuid():N}.bin");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(stream);
            return new MappedTestDocument(path);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public Span<byte> AsSpan() => GetSpan();
    public Span<byte> AsSpan(int start) => GetSpan()[start..];
    public Span<byte> AsSpan(int start, int length) => GetSpan().Slice(start, length);
    public Memory<byte> AsMemory() => Memory;
    public Memory<byte> AsMemory(int start, int length) => Memory.Slice(start, length);

    public static implicit operator ReadOnlyMemory<byte>(MappedTestDocument document)
        => document.Memory;

    public bool Equals(MappedTestDocument? other)
        => other is not null && GetSpan().SequenceEqual(other.GetSpan());

    public override bool Equals(object? obj) => obj is MappedTestDocument other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_length);

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        if (elementIndex > _length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        ReleaseMapping();
        try
        {
            File.Delete(_path);
        }
        catch when (!disposing)
        {
        }
    }

    private void ReleaseMapping()
    {
        if (_accessor is not null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _accessor = null;
        }
        _mapping?.Dispose();
        _mapping = null;
        _pointer = null;
    }
}

internal sealed class TestFileRangeSource : IRangeSource
{
    private readonly FileRangeSource _inner;
    private readonly string _ownedPath;
    private readonly System.Collections.Concurrent.ConcurrentQueue<RangeOperation> _operations = new();
    private int _disposed;

    private TestFileRangeSource(string ownedPath)
    {
        _ownedPath = ownedPath;
        _inner = FileRangeSource.Open(ownedPath);
    }

    internal static TestFileRangeSource OpenOwned(string path) => new(path);

    public long Length => _inner.Length;
    public string Generation => _inner.Generation;
    public bool LeasesAreImmutable => _inner.LeasesAreImmutable;
    public bool RetainsResidentBacking => false;
    internal RangeOperation[] Operations => _operations.ToArray();

    internal void ResetOperations()
    {
        while (_operations.TryDequeue(out _))
        {
        }
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        _operations.Enqueue(new RangeOperation(offset, destination.Length));
        await _inner.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        _operations.Enqueue(new RangeOperation(offset, length));
        return await _inner.AcquireAsync(offset, length, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _inner.DisposeAsync().ConfigureAwait(false);
        File.Delete(_ownedPath);
    }
}

internal static class MappedTestDocumentWrites
{
    internal static MappedTestDocument BuildMapped(
        this BinaryDocumentWriter builder,
        Guid? generation = null)
        => MappedTestDocument.Write(stream =>
            builder.WriteAsync(stream, generation).GetAwaiter().GetResult());

    internal static MappedTestDocument BuildMappedOverExistingTail(
        this BinaryDocumentWriter builder,
        int tailBytes)
        => MappedTestDocument.Write(stream =>
        {
            stream.SetLength(tailBytes);
            stream.Position = 0;
            builder.WriteAsync(stream).GetAwaiter().GetResult();
        });

    internal static MappedTestDocument BuildMapped(
        this AssetPackBuilder builder,
        Guid? generation = null)
        => MappedTestDocument.Write(stream =>
            builder.WriteAsync(stream, generation).GetAwaiter().GetResult());

    internal static MappedTestDocument BuildSignedMapped(
        this AssetPackBuilder builder,
        System.Security.Cryptography.RSA privateKey,
        Guid? generation = null)
        => MappedTestDocument.Write(stream =>
            builder.WriteSignedAsync(stream, privateKey, generation).GetAwaiter().GetResult());

    internal static MappedTestDocument BuildMapped(
        this AssetPackPatchBuilder builder,
        Guid? generation = null)
        => MappedTestDocument.Write(stream =>
            builder.WriteAsync(stream, generation).GetAwaiter().GetResult());

    internal static MappedTestDocument BuildSignedMapped(
        this AssetPackPatchBuilder builder,
        System.Security.Cryptography.RSA privateKey,
        Guid? generation = null)
        => MappedTestDocument.Write(stream =>
            builder.WriteSignedAsync(stream, privateKey, generation).GetAwaiter().GetResult());
}
