using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

/// <summary>
/// The backend-neutral behavior receiver. Concrete backends own all native runtime state; resource
/// identities remain non-generic and are passed back to the receiver that created them.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> This is the caller-disposed backend-runtime root. Passing it to
/// <see cref="Graphics{TBackend}"/> or the Validation Layer transfers that one disposal right; a
/// construction-time reference is not a second owner. Created Devices and Surfaces are caller-disposed
/// children, while Queues and capabilities are borrowed from their Device.</para>
/// <para>Every <see cref="Span{T}"/> and <see cref="ReadOnlySpan{T}"/> argument is consumed
/// synchronously. Input RHI objects remain caller-owned. Automatic retirement may retain them until
/// completion; Manual retirement requires the caller to keep them alive. Every operation requires
/// exact backend, Device, Queue, and CommandContext compatibility. Expected non-exceptional branches
/// are returned as Status values; invalid contracts throw argument/state exceptions, unavailable
/// capabilities throw <see cref="NotSupportedException"/>, and native failures throw
/// <see cref="GraphicsException"/>.</para>
/// <para><b>After Dispose:</b> No receiver operation is available and the backend runtime never reopens.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Construction transfers the supplied receiver's only disposal right to this
/// caller-disposed root. Returned created identities remain caller-disposed children; Queues and
/// capabilities are borrowed. Input bindings never transfer ownership of the objects they name.</para>
/// <para>All Span input is consumed synchronously. Automatic retirement may retain referenced public
/// objects through completion; Manual retirement leaves lifetime synchronization to the caller.
/// Arguments must belong to this receiver and to compatible Device, Queue, and CommandContext scopes.
/// Expected operation branches use Status values; exceptional contract, capability, device-loss, and
/// native failures use their documented exception families.</para>
/// <para><b>After Dispose:</b> Every operation is invalid and the owned receiver has been closed once.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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
