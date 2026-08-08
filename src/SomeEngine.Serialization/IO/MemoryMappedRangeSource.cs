using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SomeEngine.Serialization.IO;

/// <summary>Stable file mapping that can lend zero-copy Memory ranges through leases.</summary>
public sealed class MemoryMappedRangeSource : IRangeSource
{
    private readonly MemoryMappedFile? _mapping;
    private int _disposed;

    private MemoryMappedRangeSource(MemoryMappedFile? mapping, long length, string generation)
    {
        _mapping = mapping;
        Length = length;
        Generation = generation;
    }

    public long Length { get; }
    public string Generation { get; }
    public bool LeasesAreImmutable => true;
    public bool RetainsResidentBacking => true;

    public static MemoryMappedRangeSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var stream = new FileStream(
            fullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.RandomAccess,
                BufferSize = 1,
            });
        try
        {
            long length = stream.Length;
            string generation = $"mmap:{fullPath}:{length:X16}:{Guid.NewGuid():N}";
            if (length == 0)
            {
                stream.Dispose();
                return new MemoryMappedRangeSource(mapping: null, length, generation);
            }

            MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            return new MemoryMappedRangeSource(mapping, length, generation);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        using RangeLease lease = await AcquireAsync(offset, destination.Length, cancellationToken)
            .ConfigureAwait(false);
        lease.Memory.CopyTo(destination);
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        RangeValidation.Validate(offset, length, Length);
        if (length == 0)
            return ValueTask.FromResult(RangeLease.Borrow(ReadOnlyMemory<byte>.Empty));

        MemoryMappedFile mapping = _mapping
            ?? throw new InvalidOperationException("A non-empty range cannot be acquired from an empty mapping.");
        MemoryMappedViewAccessor accessor = mapping.CreateViewAccessor(
            offset,
            length,
            MemoryMappedFileAccess.Read);
        try
        {
            var owner = new MappedRangeOwner(accessor, length);
            return ValueTask.FromResult(RangeLease.Own(owner, length));
        }
        catch
        {
            accessor.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _mapping?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed unsafe class MappedRangeOwner : MemoryManager<byte>
    {
        private MemoryMappedViewAccessor? _accessor;
        private byte* _pointer;
        private readonly int _length;
        private int _disposed;

        internal MappedRangeOwner(MemoryMappedViewAccessor accessor, int length)
        {
            _accessor = accessor;
            _length = length;
            byte* basePointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            _pointer = basePointer + accessor.PointerOffset;
        }

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
            MemoryMappedViewAccessor? accessor = Interlocked.Exchange(ref _accessor, null);
            if (accessor is null)
                return;
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            _pointer = null;
        }
    }
}
