namespace SomeEngine.Graphics;

public enum BufferMapMode : byte
{
    Read,
    Write,
}

/// <summary>
/// An exclusive host-visible buffer mapping. Disposing the lease unmaps the native allocation and
/// makes all subsequently requested spans invalid.
/// </summary>
public ref struct BufferMapping
{
    private Span<byte> _span;
    private IBufferMappingOwner? _owner;

    internal BufferMapping(
        Span<byte> span,
        IBufferMappingOwner owner,
        BufferMapMode mode,
        BufferRange range)
    {
        _span = span;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Mode = mode;
        Range = range;
    }

    public BufferMapMode Mode { get; }
    public BufferRange Range { get; }
    public bool IsDisposed => _owner is null || _owner.IsDisposed;

    public Span<byte> Span
    {
        get
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(BufferMapping));
            return _span;
        }
    }

    public void Dispose()
    {
        IBufferMappingOwner? owner = _owner;
        _owner = null;
        _span = default;
        owner?.Dispose();
    }
}

internal interface IBufferMappingOwner : IDisposable
{
    bool IsDisposed { get; }
}
