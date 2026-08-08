namespace SomeEngine.Graphics;

public enum AdapterType : byte
{
    Other,
    Integrated,
    Discrete,
    Virtual,
    Cpu,
}

public readonly record struct AdapterId(ulong Low, ulong High)
{
    public bool IsDefault => Low == 0 && High == 0;
}

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

public enum AdapterPreference : byte
{
    Unspecified,
    HighPerformance,
    MinimumPower,
}

public readonly record struct AdapterEnumerationOptions(
    AdapterPreference Preference = AdapterPreference.Unspecified,
    bool IncludeSoftware = false);

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

public readonly record struct DeviceQueueDesc(
    QueueType Type,
    uint Count = 1,
    float Priority = 0.5f);

public readonly ref struct DeviceDesc
{
    public DeviceDesc(
        AdapterId adapterId,
        RetirementType retirementType,
        ReadOnlySpan<DeviceQueueDesc> queues,
        DeviceFeatures requiredFeatures = DeviceFeatures.None,
        DeviceFeatures optionalFeatures = DeviceFeatures.None,
        uint enabledNodeMask = 1,
        string? label = null)
    {
        AdapterId = adapterId;
        RetirementType = retirementType;
        Queues = queues;
        RequiredFeatures = requiredFeatures;
        OptionalFeatures = optionalFeatures;
        EnabledNodeMask = enabledNodeMask;
        Label = label;
    }

    public AdapterId AdapterId { get; }
    public RetirementType RetirementType { get; }
    public ReadOnlySpan<DeviceQueueDesc> Queues { get; }
    public DeviceFeatures RequiredFeatures { get; }
    public DeviceFeatures OptionalFeatures { get; }
    public uint EnabledNodeMask { get; }
    public string? Label { get; }
}

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

public sealed class DeviceCapabilities
{
    public DeviceCapabilities(
        DeviceFeatures features,
        in DeviceLimits limits,
        bool supportsBundles,
        bool supportsPipelineStatistics,
        bool supportsStreamOutputStatistics)
    {
        Features = features;
        Limits = limits;
        SupportsBundles = supportsBundles;
        SupportsPipelineStatistics = supportsPipelineStatistics;
        SupportsStreamOutputStatistics = supportsStreamOutputStatistics;
    }

    public DeviceFeatures Features { get; }
    public DeviceLimits Limits { get; }
    public bool SupportsBundles { get; }
    public bool SupportsPipelineStatistics { get; }
    public bool SupportsStreamOutputStatistics { get; }
}

public abstract class Device : GraphicsObject
{
    private readonly object _terminalGate = new();
    private int _status = (int)DeviceStatus.Active;
    private GraphicsException? _loss;

    internal Device(
        in AdapterInfo adapter,
        DeviceCapabilities capabilities,
        RetirementType retirementType,
        uint enabledNodeMask,
        string? label)
        : base(label)
    {
        Adapter = adapter;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        RetirementType = retirementType;
        EnabledNodeMask = enabledNodeMask;
    }

    public AdapterInfo Adapter { get; }
    public DeviceCapabilities Capabilities { get; }
    public RetirementType RetirementType { get; }
    public uint EnabledNodeMask { get; }
    public DeviceStatus Status => (DeviceStatus)Volatile.Read(ref _status);

    internal object RuntimeIdentity { get; init; } = null!;
    internal GraphicsException? Loss => Volatile.Read(ref _loss);

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

public abstract class DeviceCapability
{
    internal DeviceCapability(Device device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public Device Device { get; }
}

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

public enum NativeWindowType : byte
{
    Win32,
}

public readonly record struct SurfaceDesc(
    NativeWindowType Type,
    nint WindowHandle,
    nint DisplayHandle = 0,
    string? Label = null);

public abstract class Surface : GraphicsObject
{
    internal Surface(
        NativeWindowType type,
        nint windowHandle,
        nint displayHandle,
        object runtimeIdentity,
        string? label)
        : base(label)
    {
        Type = type;
        WindowHandle = windowHandle;
        DisplayHandle = displayHandle;
        RuntimeIdentity = runtimeIdentity ?? throw new ArgumentNullException(nameof(runtimeIdentity));
    }

    public NativeWindowType Type { get; }
    public nint WindowHandle { get; }
    public nint DisplayHandle { get; }
    internal object RuntimeIdentity { get; }
}
