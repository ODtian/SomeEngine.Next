namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class CalibratedTimestamps : DeviceCapability
{
    internal CalibratedTimestamps(Device device)
        : base(device)
    {
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct CalibratedTimestampInfo(
    long CpuCounter,
    long CpuFrequency,
    ulong QueueCounter,
    ulong QueueFrequency);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class LinkedAdapters : DeviceCapability
{
    internal LinkedAdapters(
        Device device,
        uint nodeCount,
        uint resourceCreationMask,
        uint resourceVisibilityMask,
        uint queueMask,
        uint pipelineMask)
        : base(device)
    {
        NodeCount = nodeCount;
        ResourceCreationMask = resourceCreationMask;
        ResourceVisibilityMask = resourceVisibilityMask;
        QueueMask = queueMask;
        PipelineMask = pipelineMask;
    }

    public uint NodeCount { get; }
    public uint ResourceCreationMask { get; }
    public uint ResourceVisibilityMask { get; }
    public uint QueueMask { get; }
    public uint PipelineMask { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ExternalHandleType : byte
{
    OpaqueWin32,
    OpaqueWin32Kmt,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum ExternalHandleTypes : byte
{
    None = 0,
    OpaqueWin32 = 1 << 0,
    OpaqueWin32Kmt = 1 << 1,
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-owned OS shared handle returned by export. Dispose closes exactly
/// that handle. Passing it to an import borrows its Value synchronously and never transfers or closes
/// the input handle.</para>
/// <para><b>After Dispose:</b> Type remains readable; Value throws
/// <see cref="ObjectDisposedException"/> and the closed OS handle is never reused.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class ExternalHandle : IDisposable
{
    private nint _value;
    private readonly Action<nint>? _release;

    internal ExternalHandle(ExternalHandleType type, nint value, Action<nint>? release)
    {
        Type = type;
        _value = value;
        _release = release;
    }

    public ExternalHandleType Type { get; }

    public nint Value
    {
        get
        {
            nint value = Volatile.Read(ref _value);
            if (value == 0)
                throw new ObjectDisposedException(nameof(ExternalHandle));
            return value;
        }
    }

    public void Dispose()
    {
        nint value = Interlocked.Exchange(ref _value, 0);
        if (value == 0)
            return;
        try
        {
            _release?.Invoke(value);
        }
        catch
        {
        }
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class ExternalResources : DeviceCapability
{
    internal ExternalResources(
        Device device,
        ExternalHandleTypes bufferImportHandleTypes,
        ExternalHandleTypes bufferExportHandleTypes,
        ExternalHandleTypes textureImportHandleTypes,
        ExternalHandleTypes textureExportHandleTypes,
        ExternalHandleTypes heapImportHandleTypes,
        ExternalHandleTypes heapExportHandleTypes)
        : base(device)
    {
        BufferImportHandleTypes = bufferImportHandleTypes;
        BufferExportHandleTypes = bufferExportHandleTypes;
        TextureImportHandleTypes = textureImportHandleTypes;
        TextureExportHandleTypes = textureExportHandleTypes;
        HeapImportHandleTypes = heapImportHandleTypes;
        HeapExportHandleTypes = heapExportHandleTypes;
    }

    public ExternalHandleTypes BufferImportHandleTypes { get; }
    public ExternalHandleTypes BufferExportHandleTypes { get; }
    public ExternalHandleTypes TextureImportHandleTypes { get; }
    public ExternalHandleTypes TextureExportHandleTypes { get; }
    public ExternalHandleTypes HeapImportHandleTypes { get; }
    public ExternalHandleTypes HeapExportHandleTypes { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class ExternalTimelines : DeviceCapability
{
    internal ExternalTimelines(
        Device device,
        ExternalHandleTypes importHandleTypes,
        ExternalHandleTypes exportHandleTypes)
        : base(device)
    {
        ImportHandleTypes = importHandleTypes;
        ExportHandleTypes = exportHandleTypes;
    }

    public ExternalHandleTypes ImportHandleTypes { get; }
    public ExternalHandleTypes ExportHandleTypes { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct ImportedResourceState(
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout,
    QueueType QueueType);
