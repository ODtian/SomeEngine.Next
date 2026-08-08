using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

/// <summary>
/// The backend-neutral behavior receiver. Concrete backends own all native runtime state; resource
/// identities remain non-generic and are passed back to the receiver that created them.
/// </summary>
public partial interface IGraphicsBackend : IDisposable
{
    bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount);

    Device CreateDevice(in DeviceDesc desc);
    Surface CreateSurface(in SurfaceDesc desc);
}

internal interface INativeValidationControl
{
    void EnableNativeValidation();
}

/// <summary>
/// Owns one concrete receiver while preserving its closed type through behavior-execution code.
/// </summary>
public sealed partial class Graphics<TBackend> : IDisposable
    where TBackend : class, IGraphicsBackend
{
    private readonly TBackend _backend;
    private int _disposeState;

    public Graphics(TBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    internal TBackend Receiver
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _backend;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount) =>
        Receiver.TryEnumerateAdapters(options, destination, out requiredCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Device CreateDevice(in DeviceDesc desc) => Receiver.CreateDevice(desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Surface CreateSurface(in SurfaceDesc desc) => Receiver.CreateSurface(desc);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            _backend.Dispose();
    }
}
