namespace SomeEngine.Assets;

/// <summary>
/// A scoped read of the current value behind an <see cref="AssetHandle{T}"/>. Disposal releases
/// the loader's read admission so unload and replacement can wait without retaining two values.
/// </summary>
public sealed class AssetRead<T> : IDisposable
    where T : class
{
    private AssetHandleState<T>? _owner;
    private T? _value;

    internal AssetRead(AssetHandleState<T> owner, T value, ulong revision)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _value = value ?? throw new ArgumentNullException(nameof(value));
        Revision = revision;
    }

    /// <summary>The unique asset value published for this read's revision.</summary>
    public T Value => Volatile.Read(ref _value)
        ?? throw new ObjectDisposedException(nameof(AssetRead<T>));

    /// <summary>The revision fixed when the read was acquired.</summary>
    public ulong Revision { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _value, null);
        AssetHandleState<T>? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;
        owner.ReleaseRead();
        GC.SuppressFinalize(this);
    }

    ~AssetRead()
    {
        try
        {
            Interlocked.Exchange(ref _value, null);
            Interlocked.Exchange(ref _owner, null)?.ReleaseRead();
        }
        catch
        {
        }
    }
}
