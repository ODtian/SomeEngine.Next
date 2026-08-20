namespace SomeEngine.Graphics;

/// <summary>
/// Indicates that a Device was created with presentation support and can create
/// swapchains for compatible surfaces.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable metadata may be shared.</para>
/// <para><b>Ownership:</b> Borrowed immutable metadata owned by its Device.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state and does not revive its disposed Device.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class Presentation : DeviceCapability
{
    internal Presentation(Device device)
        : base(device)
    {
    }
}

/// <summary>Optional advanced modes exposed by a Device's native Pipeline implementation.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>Basic asynchronous Pipeline creation is part of <see cref="IGraphicsBackend"/> and does
/// not require any flag in this enum. These flags describe only optional native behavior.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum PipelineCreationFeatures : byte
{
    None = 0,
    PersistentCacheData = 1 << 0,
    CompileRequiredDetection = 1 << 1,
    PipelineSpecialization = 1 << 2,
}

/// <summary>Reports optional advanced native Pipeline creation behavior.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable metadata may be shared.</para>
/// <para><b>Ownership:</b> Borrowed immutable metadata owned by its Device.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state and does not revive its disposed Device.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class PipelineCreationSupport : DeviceCapability
{
    internal PipelineCreationSupport(
        Device device,
        PipelineCreationFeatures features)
        : base(device) => Features = features;

    public PipelineCreationFeatures Features { get; }
}

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
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-owned OS shared handle returned by export. Dispose closes exactly
/// that handle. Passing it to an import borrows its Value synchronously and never transfers or closes
/// the input handle.</para>
/// <para><b>After Dispose:</b> Type remains readable; Value throws
/// <see cref="ObjectDisposedException"/> and the closed OS handle is never reused.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class ExternalHandle : IDisposable
{
    private DisposeGate _disposeGate;
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
        if (!_disposeGate.TryEnter())
            return;
        try
        {
            nint value = Interlocked.Exchange(ref _value, 0);
            if (value != 0)
                _release?.Invoke(value);
        }
        catch
        {
        }
        finally
        {
            _disposeGate.Exit();
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
    private readonly ExternalHandleType[] _bufferImportHandleTypes;
    private readonly ExternalHandleType[] _bufferExportHandleTypes;
    private readonly ExternalHandleType[] _textureImportHandleTypes;
    private readonly ExternalHandleType[] _textureExportHandleTypes;
    private readonly ExternalHandleType[] _heapImportHandleTypes;
    private readonly ExternalHandleType[] _heapExportHandleTypes;

    internal ExternalResources(
        Device device,
        ReadOnlySpan<ExternalHandleType> bufferImportHandleTypes,
        ReadOnlySpan<ExternalHandleType> bufferExportHandleTypes,
        ReadOnlySpan<ExternalHandleType> textureImportHandleTypes,
        ReadOnlySpan<ExternalHandleType> textureExportHandleTypes,
        ReadOnlySpan<ExternalHandleType> heapImportHandleTypes,
        ReadOnlySpan<ExternalHandleType> heapExportHandleTypes)
        : base(device)
    {
        _bufferImportHandleTypes = ValidateHandleTypes(bufferImportHandleTypes);
        _bufferExportHandleTypes = ValidateHandleTypes(bufferExportHandleTypes);
        _textureImportHandleTypes = ValidateHandleTypes(textureImportHandleTypes);
        _textureExportHandleTypes = ValidateHandleTypes(textureExportHandleTypes);
        _heapImportHandleTypes = ValidateHandleTypes(heapImportHandleTypes);
        _heapExportHandleTypes = ValidateHandleTypes(heapExportHandleTypes);
    }

    public bool SupportsBufferImport(ExternalHandleType type) =>
        Contains(_bufferImportHandleTypes, type);
    public bool SupportsBufferExport(ExternalHandleType type) =>
        Contains(_bufferExportHandleTypes, type);
    public bool SupportsTextureImport(ExternalHandleType type) =>
        Contains(_textureImportHandleTypes, type);
    public bool SupportsTextureExport(ExternalHandleType type) =>
        Contains(_textureExportHandleTypes, type);
    public bool SupportsHeapImport(ExternalHandleType type) =>
        Contains(_heapImportHandleTypes, type);
    public bool SupportsHeapExport(ExternalHandleType type) =>
        Contains(_heapExportHandleTypes, type);

    private static ExternalHandleType[] ValidateHandleTypes(
        ReadOnlySpan<ExternalHandleType> handleTypes)
    {
        ExternalHandleType[] result = handleTypes.ToArray();
        foreach (ExternalHandleType type in result)
        {
            if (!Enum.IsDefined(type))
                throw new ArgumentOutOfRangeException(nameof(handleTypes));
        }
        return result;
    }

    private static bool Contains(
        ExternalHandleType[] handleTypes,
        ExternalHandleType type) =>
        Enum.IsDefined(type) && Array.IndexOf(handleTypes, type) >= 0;
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class ExternalTimelines : DeviceCapability
{
    private readonly ExternalHandleType[] _importHandleTypes;
    private readonly ExternalHandleType[] _exportHandleTypes;

    internal ExternalTimelines(
        Device device,
        ReadOnlySpan<ExternalHandleType> importHandleTypes,
        ReadOnlySpan<ExternalHandleType> exportHandleTypes)
        : base(device)
    {
        _importHandleTypes = ValidateHandleTypes(importHandleTypes);
        _exportHandleTypes = ValidateHandleTypes(exportHandleTypes);
    }

    public bool SupportsImport(ExternalHandleType type) =>
        Contains(_importHandleTypes, type);
    public bool SupportsExport(ExternalHandleType type) =>
        Contains(_exportHandleTypes, type);

    private static ExternalHandleType[] ValidateHandleTypes(
        ReadOnlySpan<ExternalHandleType> handleTypes)
    {
        ExternalHandleType[] result = handleTypes.ToArray();
        foreach (ExternalHandleType type in result)
        {
            if (!Enum.IsDefined(type))
                throw new ArgumentOutOfRangeException(nameof(handleTypes));
        }
        return result;
    }

    private static bool Contains(
        ExternalHandleType[] handleTypes,
        ExternalHandleType type) =>
        Enum.IsDefined(type) && Array.IndexOf(handleTypes, type) >= 0;
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
