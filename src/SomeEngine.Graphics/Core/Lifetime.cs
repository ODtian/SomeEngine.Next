namespace SomeEngine.Graphics;

/// <summary>Shared terminal/idempotent lifetime gate for caller-disposable RHI identities.</summary>
public abstract class GraphicsObject : IDisposable
{
    private int _disposed;

    internal GraphicsObject(string? label)
    {
        Label = label;
    }

    public string? Label { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }

    internal void DisposeFromParent()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            Release(fromParent: true);
        }
        catch (Exception exception)
        {
            RecordReleaseFailure(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            Release(fromParent: false);
        }
        catch (Exception exception)
        {
            RecordReleaseFailure(exception);
        }
    }

    internal abstract void Release(bool fromParent);

    internal virtual void RecordReleaseFailure(Exception exception)
    {
    }
}

/// <summary>A caller-disposable identity associated with exactly one Device.</summary>
public abstract class DeviceResource : GraphicsObject
{
    internal DeviceResource(Device device, string? label)
        : base(label)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public Device Device { get; }
}
