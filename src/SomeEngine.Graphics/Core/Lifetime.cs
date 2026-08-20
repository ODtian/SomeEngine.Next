namespace SomeEngine.Graphics;

/// <summary>
/// Allocation-free owner/join state for the cold release path of a public disposable identity.
/// </summary>
internal struct DisposeGate
{
    private const int Alive = 0;
    private const int Releasing = 1;
    private const int Released = 2;

    private int _state;
    private int _ownerThreadId;

    internal bool IsDisposed => Volatile.Read(ref _state) != Alive;

    internal bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _state, Releasing, Alive) == Alive)
        {
            Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
            return true;
        }

        if (Volatile.Read(ref _ownerThreadId) == Environment.CurrentManagedThreadId)
            return false;

        Join();
        return false;
    }

    internal void Exit()
    {
        Volatile.Write(ref _state, Released);
        Volatile.Write(ref _ownerThreadId, 0);
    }

    private void Join()
    {
        if (Volatile.Read(ref _state) == Released)
            return;
        var spinner = new SpinWait();
        while (Volatile.Read(ref _state) != Released)
            spinner.SpinOnce();
    }
}

/// <summary>Shared terminal/idempotent lifetime gate for caller-disposable RHI identities.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class GraphicsObject : IDisposable
{
    private DisposeGate _disposeGate;

    internal GraphicsObject? DeviceLossWorkNext;
    internal GraphicsObject? RegistryDrainNext;
    internal GraphicsObject? SecondaryRegistryDrainNext;

    internal GraphicsObject(string? label)
    {
        Label = label;
    }

    public string? Label { get; }

    internal bool IsDisposed => _disposeGate.IsDisposed;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }

    internal void DisposeFromParent()
    {
        DisposeCore(fromParent: true);
    }

    public void Dispose()
    {
        DisposeCore(fromParent: false);
    }

    private void DisposeCore(bool fromParent)
    {
        if (!_disposeGate.TryEnter())
            return;
        try
        {
            Release(fromParent);
        }
        catch (Exception exception)
        {
            try
            {
                RecordReleaseFailure(exception);
            }
            catch
            {
            }
        }
        finally
        {
            _disposeGate.Exit();
        }
    }

    internal abstract void Release(bool fromParent);

    internal virtual void RecordReleaseFailure(Exception exception)
    {
    }
}

/// <summary>A caller-disposable identity associated with exactly one Device.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class DeviceResource : GraphicsObject
{
    internal DeviceResource(Device device, string? label)
        : base(label)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public Device Device { get; }

    internal override void RecordReleaseFailure(Exception exception) =>
        Device.RecordReleaseFailure(exception);
}
