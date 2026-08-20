namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AdapterType : byte
{
    Other,
    Integrated,
    Discrete,
    Virtual,
    Cpu,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AdapterId(ulong Low, ulong High)
{
    public bool IsDefault => Low == 0 && High == 0;
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AdapterInfo(
    AdapterId Id,
    AdapterType Type,
    string Name,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemory,
    ulong DedicatedSystemMemory,
    ulong SharedSystemMemory,
    string DriverVersion,
    bool HardwareAccelerated);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AdapterPreference : byte
{
    Unspecified,
    HighPerformance,
    MinimumPower,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AdapterEnumerationOptions(
    AdapterPreference Preference = AdapterPreference.Unspecified,
    bool IncludeSoftware = false);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>These flags request capabilities while creating a Device. They are not runtime capability
/// facts; after creation, callers must use <see cref="IGraphicsBackend.TryGetCapability{TCapability}"/>
/// and the returned typed capability.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum DeviceFeatures : ulong
{
    None = 0,
    Presentation = 1UL << 0,
    SparseResources = 1UL << 1,
    SamplerFeedback = 1UL << 2,
    Residency = 1UL << 3,
    RayTracing = 1UL << 4,
    MeshShaders = 1UL << 5,
    VariableRateShading = 1UL << 6,
    WorkGraphs = 1UL << 7,
    IndirectCommands = 1UL << 8,
    CalibratedTimestamps = 1UL << 9,
    LinkedAdapters = 1UL << 10,
    ExternalResources = 1UL << 11,
    ExternalTimelines = 1UL << 12,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DeviceQueueDesc(
    QueueType Type,
    uint Count = 1,
    float Priority = 0.5f,
    uint NodeIndex = 0);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct DeviceDesc
{
    public DeviceDesc(
        AdapterId adapterId,
        ReadOnlySpan<DeviceQueueDesc> queues,
        DeviceFeatures requiredFeatures = DeviceFeatures.None,
        DeviceFeatures optionalFeatures = DeviceFeatures.None,
        uint enabledNodeMask = 1,
        string? label = null)
    {
        AdapterId = adapterId;
        Queues = queues;
        RequiredFeatures = requiredFeatures;
        OptionalFeatures = optionalFeatures;
        EnabledNodeMask = enabledNodeMask;
        Label = label;
    }

    public AdapterId AdapterId { get; }
    public ReadOnlySpan<DeviceQueueDesc> Queues { get; }
    /// <summary>Capability requests that must be satisfied for Device creation to succeed.</summary>
    public DeviceFeatures RequiredFeatures { get; }

    /// <summary>Capability requests enabled only when the selected adapter supports them.</summary>
    public DeviceFeatures OptionalFeatures { get; }
    public uint EnabledNodeMask { get; }
    public string? Label { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DeviceLimits(
    ulong MaximumBufferSize,
    uint MaximumTextureDimension1D,
    uint MaximumTextureDimension2D,
    uint MaximumTextureDimension3D,
    uint MaximumTextureArrayLayers,
    uint MaximumColorAttachments,
    uint MaximumViewports,
    uint ResourceDescriptorCapacity,
    uint SamplerDescriptorCapacity,
    uint ConstantBufferAlignment,
    uint TextureDataPitchAlignment,
    uint TextureDataPlacementAlignment);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum FormatFeatures : uint
{
    None = 0,
    Buffer = 1u << 0,
    VertexBuffer = 1u << 1,
    IndexBuffer = 1u << 2,
    StreamOutput = 1u << 3,
    Texture1D = 1u << 4,
    Texture2D = 1u << 5,
    Texture3D = 1u << 6,
    TextureCube = 1u << 7,
    ShaderLoad = 1u << 8,
    ShaderSample = 1u << 9,
    ShaderSampleComparison = 1u << 10,
    Mipmaps = 1u << 11,
    ColorAttachment = 1u << 12,
    ColorAttachmentBlend = 1u << 13,
    DepthStencilAttachment = 1u << 14,
    MultisampleColorAttachment = 1u << 15,
    MultisampleLoad = 1u << 16,
    MultisampleResolve = 1u << 17,
    Storage = 1u << 18,
    StorageLoad = 1u << 19,
    StorageStore = 1u << 20,
    StorageAtomic = 1u << 21,
    LogicOperation = 1u << 22,
    SparseTexture2D = 1u << 23,
    SparseTexture3D = 1u << 24,
    SamplerFeedbackTarget = 1u << 25,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum SampleCounts : byte
{
    None = 0,
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8,
    Sixteen = 16,
    ThirtyTwo = 32,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct FormatSupport(
    Format Format,
    FormatFeatures Features,
    SampleCounts SupportedSampleCounts,
    SampleCounts SupportedSparseSampleCounts)
{
    private const uint MaximumSampleCount = 32;

    public bool SupportsSampleCount(uint sampleCount) =>
        TryGetSampleCount(sampleCount, out SampleCounts value) &&
        (SupportedSampleCounts & value) != 0;

    public bool SupportsSparseSampleCount(uint sampleCount) =>
        TryGetSampleCount(sampleCount, out SampleCounts value) &&
        (SupportedSparseSampleCounts & value) != 0;

    private static bool TryGetSampleCount(uint sampleCount, out SampleCounts value)
    {
        value = sampleCount switch
        {
            1 => SomeEngine.Graphics.SampleCounts.One,
            2 => SomeEngine.Graphics.SampleCounts.Two,
            4 => SomeEngine.Graphics.SampleCounts.Four,
            8 => SomeEngine.Graphics.SampleCounts.Eight,
            16 => SomeEngine.Graphics.SampleCounts.Sixteen,
            MaximumSampleCount => SomeEngine.Graphics.SampleCounts.ThirtyTwo,
            _ => SomeEngine.Graphics.SampleCounts.None,
        };
        return value != SomeEngine.Graphics.SampleCounts.None;
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed immutable metadata owned by its associated Device or resource; callers never Dispose it.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state and does not revive its disposed owner.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class DeviceCapabilities
{
    private readonly FormatSupport[] _formats;

    public DeviceCapabilities(
        in DeviceLimits limits,
        bool supportsBundles,
        bool supportsPipelineStatistics,
        bool supportsStreamOutputStatistics,
        bool supportsDepthBounds,
        DynamicStates supportedDynamicStates,
        ReadOnlySpan<FormatSupport> formats)
    {
        Limits = limits;
        SupportsBundles = supportsBundles;
        SupportsPipelineStatistics = supportsPipelineStatistics;
        SupportsStreamOutputStatistics = supportsStreamOutputStatistics;
        SupportsDepthBounds = supportsDepthBounds;
        const DynamicStates knownDynamicStates =
            DynamicStates.Viewport |
            DynamicStates.Scissor |
            DynamicStates.BlendConstants |
            DynamicStates.StencilReference |
            DynamicStates.DepthBounds |
            DynamicStates.DepthBias |
            DynamicStates.PrimitiveTopology |
            DynamicStates.StripCut;
        if ((supportedDynamicStates & ~knownDynamicStates) != 0)
            throw new ArgumentOutOfRangeException(nameof(supportedDynamicStates));
        SupportedDynamicStates = supportedDynamicStates;
        _formats = formats.ToArray();

        Format[] definedFormats = Enum.GetValues<Format>();
        if (_formats.Length != definedFormats.Length)
            throw new ArgumentException("The format support table must contain every Format exactly once.", nameof(formats));
        for (int index = 0; index < _formats.Length; index++)
        {
            if (_formats[index].Format != definedFormats[index])
            {
                throw new ArgumentException(
                    "The format support table must follow the canonical Format declaration order.",
                    nameof(formats));
            }
        }
    }

    public DeviceLimits Limits { get; }
    public bool SupportsBundles { get; }
    public bool SupportsPipelineStatistics { get; }
    public bool SupportsStreamOutputStatistics { get; }
    public bool SupportsDepthBounds { get; }
    public DynamicStates SupportedDynamicStates { get; }
    public ReadOnlySpan<FormatSupport> Formats => _formats;

    public FormatSupport GetFormatSupport(Format format)
    {
        int index = (int)format - (int)_formats[0].Format;
        if ((uint)index >= (uint)_formats.Length || _formats[index].Format != format)
            throw new ArgumentOutOfRangeException(nameof(format));
        return _formats[index];
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Concurrent Dispose calls are safe and collectively perform one logical release; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class Device : GraphicsObject
{
    private readonly object _terminalGate = new();
    private int _status = (int)DeviceStatus.Active;
    private GraphicsException? _loss;
    private Exception? _teardownFailure;

    internal Device(
        in AdapterInfo adapter,
        DeviceCapabilities capabilities,
        uint enabledNodeMask,
        string? label)
        : base(label)
    {
        Adapter = adapter;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        EnabledNodeMask = enabledNodeMask;
    }

    public AdapterInfo Adapter { get; }
    public DeviceCapabilities Capabilities { get; }
    public uint EnabledNodeMask { get; }
    public DeviceStatus Status => (DeviceStatus)Volatile.Read(ref _status);

    internal IGraphicsBackend BackendOwner { get; init; } = null!;
    internal GraphicsException? Loss => Volatile.Read(ref _loss);
    internal Exception? TeardownFailure => Volatile.Read(ref _teardownFailure);

    internal override void RecordReleaseFailure(Exception exception) =>
        Interlocked.CompareExchange(ref _teardownFailure, exception, null);

    internal void ThrowIfUnavailable()
    {
        switch (Status)
        {
            case DeviceStatus.Active:
                return;
            case DeviceStatus.Lost:
                throw _loss ?? new GraphicsException(
                    GraphicsError.DeviceLost,
                    "The graphics device is lost.");
            case DeviceStatus.Disposed:
                throw new ObjectDisposedException(GetType().FullName);
            default:
                throw new InvalidOperationException("The graphics device has an invalid status.");
        }
    }

    internal bool TryMarkLost(GraphicsException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Error != GraphicsError.DeviceLost)
            throw new ArgumentException("A terminal device loss must use GraphicsError.DeviceLost.", nameof(exception));

        lock (_terminalGate)
        {
            if ((DeviceStatus)_status != DeviceStatus.Active)
                return false;

            Volatile.Write(ref _loss, exception);
            Volatile.Write(ref _status, (int)DeviceStatus.Lost);
            return true;
        }
    }

    internal void MarkDisposed()
    {
        lock (_terminalGate)
            Volatile.Write(ref _status, (int)DeviceStatus.Disposed);
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed immutable metadata owned by its associated Device or resource; callers never Dispose it.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state and does not revive its disposed owner.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class DeviceCapability
{
    internal DeviceCapability(Device device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public Device Device { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed Device-owned Queue; callers never Dispose it.</para>
/// <para><b>After Dispose:</b> The Queue has no Dispose operation; parent Device disposal invalidates submission and native access while immutable provenance remains readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class Queue
{
    internal Queue(
        Device device,
        QueueType type,
        uint index,
        float priority,
        uint nodeIndex)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Type = type;
        Index = index;
        Priority = priority;
        NodeIndex = nodeIndex;
    }

    public Device Device { get; }
    public QueueType Type { get; }
    public uint Index { get; }
    public float Priority { get; }
    public uint NodeIndex { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum NativeWindowType : byte
{
    Win32,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct SurfaceDesc(
    NativeWindowType Type,
    nint WindowHandle,
    nint DisplayHandle = 0,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class Surface : GraphicsObject
{
    internal Surface(
        NativeWindowType type,
        nint windowHandle,
        nint displayHandle,
        IGraphicsBackend backendOwner,
        string? label)
        : base(label)
    {
        Type = type;
        WindowHandle = windowHandle;
        DisplayHandle = displayHandle;
        BackendOwner = backendOwner ?? throw new ArgumentNullException(nameof(backendOwner));
    }

    public NativeWindowType Type { get; }
    public nint WindowHandle { get; }
    public nint DisplayHandle { get; }
    internal IGraphicsBackend BackendOwner { get; }
}
