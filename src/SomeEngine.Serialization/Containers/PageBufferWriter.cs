using System.Buffers;

namespace SomeEngine.Serialization.Containers;

/// <summary>
/// A standard encoder destination. GetMemory/GetSpan expose reusable staging memory; Advance
/// copies only that committed range into pages. Escaped writable memory can never change a
/// retained read or branch. No complete encoded value is staged by this adapter.
/// </summary>
public sealed class PageBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly PageBuffer _buffer;
    private byte[] _staging = [];
    private bool _disposed;
    private long _written;

    public PageBufferWriter(PageArena arena) => _buffer = new(arena);
    public long WrittenCount
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _written; }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int size = Math.Max(sizeHint, 256);
        if (_staging.Length < size) _staging = new byte[size];
        return _staging;
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)count > (uint)_staging.Length) throw new ArgumentOutOfRangeException(nameof(count));
        long required = checked(_written + count);
        long capacity = _buffer.Length;
        if (required > capacity)
        {
            int growth = (int)Math.Min(_buffer.Arena.MaximumPageSize, Math.Max(capacity, required - capacity));
            long extra = Math.Max(required - capacity, _buffer.Arena.SizeFor(growth));
            _buffer.Resize(checked(capacity + extra));
        }
        _buffer.Write(_written, _staging.AsSpan(0, count));
        _written = required;
    }

    public PageBuffer Duplicate() => _buffer.Slice(0, WrittenCount);
    public PageRange<byte> Retain() => _buffer.Retain(0, WrittenCount);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.Dispose();
        _staging = [];
    }
}
