namespace SomeEngine.Graphics;

public sealed class CalibratedTimestamps : DeviceCapability
{
    internal CalibratedTimestamps(Device device)
        : base(device)
    {
    }
}

public readonly record struct CalibratedTimestampInfo(
    long CpuCounter,
    long CpuFrequency,
    ulong QueueCounter,
    ulong QueueFrequency);

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

public enum ExternalHandleType : byte
{
    OpaqueWin32,
    OpaqueWin32Kmt,
}

[Flags]
public enum ExternalHandleTypes : byte
{
    None = 0,
    OpaqueWin32 = 1 << 0,
    OpaqueWin32Kmt = 1 << 1,
}

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

public readonly record struct ImportedResourceState(
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout,
    QueueType QueueType);
