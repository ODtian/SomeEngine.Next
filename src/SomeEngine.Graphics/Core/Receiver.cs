namespace SomeEngine.Graphics;

/// <summary>
/// The backend-neutral behavior receiver. Concrete backends own all native runtime state; resource
/// identities remain non-generic and are passed back to the receiver that created them.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Concurrent Dispose calls are safe and collectively perform one logical release; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> This is the caller-disposed backend-runtime root. Passing it to the
/// Validation Layer transfers that one disposal right; a construction-time reference is not a second
/// owner. Created Devices and Surfaces are caller-disposed children, while Queues and capabilities are
/// borrowed from their Device.</para>
/// <para>Every <see cref="Span{T}"/> and <see cref="ReadOnlySpan{T}"/> argument is consumed
/// synchronously. Input RHI wrappers remain caller-owned. Once a Queue accepts recorded work, the
/// backend retains every native dependency required for execution until that submission completes.
/// Every operation requires
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
