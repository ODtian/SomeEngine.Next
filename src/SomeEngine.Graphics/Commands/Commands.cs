using System.Numerics;

namespace SomeEngine.Graphics;

public readonly record struct CommandContextDesc(
    QueueType QueueType,
    uint QueueIndex,
    uint NodeIndex,
    uint InitialSlotCount,
    bool Bundle = false,
    string? Label = null);

public readonly record struct CommandRecordingDesc(
    uint InitialResourceDescriptorCapacity = 0,
    uint InitialSamplerDescriptorCapacity = 0,
    uint InitialCapturedResourceCapacity = 0,
    string? Label = null);

public abstract class CommandContext : DeviceResource
{
    internal CommandContext(
        Device device,
        QueueType queueType,
        uint queueIndex,
        uint nodeIndex,
        bool bundle,
        string? label)
        : base(device, label)
    {
        QueueType = queueType;
        QueueIndex = queueIndex;
        NodeIndex = nodeIndex;
        Bundle = bundle;
    }

    public QueueType QueueType { get; }
    public uint QueueIndex { get; }
    public uint NodeIndex { get; }
    public bool Bundle { get; }
}

internal abstract class RecordedCommandsLease
{
    private readonly object _gate = new();
    private ulong _sequence;
    private RecordedCommandsStatus _status = RecordedCommandsStatus.Discarded;
    private bool _callerDisposed = true;

    protected RecordedCommandsLease(Device device, Queue queue)
    {
        Device = device;
        Queue = queue;
    }

    internal Device Device { get; }
    internal Queue Queue { get; }
    internal RecordedCommandsStatus GetStatus(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            return _callerDisposed ? RecordedCommandsStatus.Disposed : _status;
        }
    }

    protected void Activate(ulong sequence)
    {
        if (sequence == 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        lock (_gate)
        {
            if (_sequence != 0 &&
                _status is RecordedCommandsStatus.Executable or
                    RecordedCommandsStatus.Submitting or
                    RecordedCommandsStatus.Submitted)
            {
                throw new InvalidOperationException("The command payload is still active.");
            }
            _sequence = sequence;
            _status = RecordedCommandsStatus.Executable;
            _callerDisposed = false;
        }
    }

    internal bool TryBeginSubmit(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            if (_callerDisposed || _status != RecordedCommandsStatus.Executable)
                return false;
            _status = RecordedCommandsStatus.Submitting;
            return true;
        }
    }

    internal void RestoreExecutable(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            if (_status != RecordedCommandsStatus.Submitting)
                throw new InvalidOperationException("The command payload cannot be restored to Executable.");
            _status = RecordedCommandsStatus.Executable;
        }
    }

    internal void MarkSubmitted(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            if (_status != RecordedCommandsStatus.Submitting)
                throw new InvalidOperationException("The command payload is not being submitted.");
            _status = RecordedCommandsStatus.Submitted;
        }
    }

    internal void MarkCompleted(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            if (_status == RecordedCommandsStatus.Submitted)
                _status = RecordedCommandsStatus.Completed;
        }
    }

    internal void MarkDeviceLost(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            _status = RecordedCommandsStatus.DeviceLost;
        }
    }

    protected bool TryMarkDeviceLostFromDevice(out ulong sequence, out bool abandon)
    {
        lock (_gate)
        {
            sequence = _sequence;
            abandon = false;
            switch (_status)
            {
                case RecordedCommandsStatus.Executable:
                    _status = RecordedCommandsStatus.DeviceLost;
                    abandon = true;
                    return true;
                case RecordedCommandsStatus.Submitting:
                case RecordedCommandsStatus.Submitted:
                    _status = RecordedCommandsStatus.DeviceLost;
                    return true;
                default:
                    return false;
            }
        }
    }

    protected bool TryDiscardExecutableFromDevice(out ulong sequence)
    {
        lock (_gate)
        {
            sequence = _sequence;
            if (_status != RecordedCommandsStatus.Executable)
                return false;

            _status = RecordedCommandsStatus.Discarded;
            return true;
        }
    }

    protected void EnsureSequence(ulong sequence)
    {
        lock (_gate)
            EnsureSequenceUnderGate(sequence);
    }

    internal void DisposeCaller(ulong sequence)
    {
        bool discard = false;
        lock (_gate)
        {
            if (_sequence != sequence || _callerDisposed)
                return;
            _callerDisposed = true;
            if (_status == RecordedCommandsStatus.Executable)
            {
                _status = RecordedCommandsStatus.Discarded;
                discard = true;
            }
        }
        if (discard)
            DiscardUnsubmitted(sequence);
    }

    protected abstract void DiscardUnsubmitted(ulong sequence);

    private void EnsureSequenceUnderGate(ulong sequence)
    {
        if (sequence == 0 || _sequence != sequence)
            throw new InvalidOperationException("The RecordedCommands sequence is no longer current.");
    }
}

public readonly struct RecordedCommands : IDisposable
{
    private readonly RecordedCommandsLease? _lease;
    private readonly ulong _sequence;

    internal RecordedCommands(RecordedCommandsLease lease, ulong sequence)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _sequence = sequence != 0
            ? sequence
            : throw new ArgumentOutOfRangeException(nameof(sequence));
    }

    internal RecordedCommandsLease Lease => _lease
        ?? throw new InvalidOperationException("The default RecordedCommands is invalid.");
    internal ulong Sequence => _sequence != 0
        ? _sequence
        : throw new InvalidOperationException("The default RecordedCommands is invalid.");

    public Device Device => Lease.Device;
    public Queue Queue => Lease.Queue;
    public RecordedCommandsStatus Status => Lease.GetStatus(Sequence);
    public void Dispose()
    {
        if (_lease is not null)
            _lease.DisposeCaller(_sequence);
    }
}

public abstract class RecordedBundle : DeviceResource
{
    internal RecordedBundle(Device device, string? label)
        : base(device, label)
    {
    }
}

public readonly ref struct QueueSubmitDesc
{
    public QueueSubmitDesc(
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<RecordedCommands> commands,
        ReadOnlySpan<SwapchainImage> swapchainImages,
        ReadOnlySpan<TimelineSignal> timelineSignals)
    {
        CompletionWaits = completionWaits;
        TimelineWaits = timelineWaits;
        Commands = commands;
        SwapchainImages = swapchainImages;
        TimelineSignals = timelineSignals;
    }

    public ReadOnlySpan<QueueCompletion> CompletionWaits { get; }
    public ReadOnlySpan<TimelinePoint> TimelineWaits { get; }
    public ReadOnlySpan<RecordedCommands> Commands { get; }
    public ReadOnlySpan<SwapchainImage> SwapchainImages { get; }
    public ReadOnlySpan<TimelineSignal> TimelineSignals { get; }
}

public readonly record struct MemoryBarrier(
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter);

public readonly record struct BufferBarrier(
    Buffer Buffer,
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter);

public readonly record struct TextureBarrier(
    Texture Texture,
    TextureSubresourceRange Range,
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter,
    TextureLayout LayoutBefore,
    TextureLayout LayoutAfter);

public readonly record struct AliasingResource(
    Resource Resource,
    TextureSubresourceRange? TextureRange = null);

public readonly ref struct AliasingBarrier
{
    public AliasingBarrier(
        ReadOnlySpan<AliasingResource> before,
        ReadOnlySpan<AliasingResource> after)
    {
        Before = before;
        After = after;
    }

    public ReadOnlySpan<AliasingResource> Before { get; }
    public ReadOnlySpan<AliasingResource> After { get; }
}

public readonly record struct QueueRelease(
    Resource Resource,
    TextureSubresourceRange? TextureRange,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout,
    QueueType DestinationQueueType);

public readonly record struct QueueAcquire(
    Resource Resource,
    TextureSubresourceRange? TextureRange,
    QueueType SourceQueueType,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout);

public readonly record struct BufferCopy(
    Buffer Source,
    ulong SourceOffset,
    Buffer Destination,
    ulong DestinationOffset,
    ulong Size);

public readonly record struct BufferTextureCopy(
    Buffer Buffer,
    ulong BufferOffset,
    uint BufferRowPitch,
    uint BufferImageHeight,
    Texture Texture,
    uint MipLevel,
    uint ArrayLayer,
    TextureAspects Aspect,
    uint X,
    uint Y,
    uint Z,
    uint Width,
    uint Height,
    uint Depth);

public readonly record struct TextureCopy(
    Texture Source,
    uint SourceMipLevel,
    uint SourceArrayLayer,
    TextureAspects SourceAspect,
    uint SourceX,
    uint SourceY,
    uint SourceZ,
    Texture Destination,
    uint DestinationMipLevel,
    uint DestinationArrayLayer,
    TextureAspects DestinationAspect,
    uint DestinationX,
    uint DestinationY,
    uint DestinationZ,
    uint Width,
    uint Height,
    uint Depth);

public enum ResolveType : byte
{
    Average,
    Minimum,
    Maximum,
    SampleZero,
}

public readonly record struct TextureResolve(
    Texture Source,
    uint SourceMipLevel,
    uint SourceArrayLayer,
    Texture Destination,
    uint DestinationMipLevel,
    uint DestinationArrayLayer,
    Format Format,
    ResolveType Type = ResolveType.Average);

public enum LoadType : byte
{
    Load,
    Clear,
    Discard,
}

public enum StoreType : byte
{
    Store,
    Discard,
}

public readonly record struct ColorAttachmentDesc(
    ColorAttachmentView View,
    LoadType Load,
    StoreType Store,
    Vector4 ClearValue,
    ColorAttachmentView? ResolveView = null,
    ResolveType ResolveType = ResolveType.Average);

public readonly record struct DepthStencilAttachmentDesc(
    DepthStencilView View,
    LoadType DepthLoad,
    StoreType DepthStore,
    LoadType StencilLoad,
    StoreType StencilStore,
    float ClearDepth = 1,
    byte ClearStencil = 0);

[Flags]
public enum RenderingOptions : byte
{
    None = 0,
    AllowUnorderedAccessWrites = 1 << 0,
    Suspending = 1 << 1,
    Resuming = 1 << 2,
}

public readonly ref struct RenderingDesc
{
    public RenderingDesc(
        ReadOnlySpan<ColorAttachmentDesc> colors,
        DepthStencilAttachmentDesc? depthStencil,
        uint width,
        uint height,
        RenderingOptions options = RenderingOptions.None)
    {
        Colors = colors;
        DepthStencil = depthStencil;
        Width = width;
        Height = height;
        Options = options;
    }

    public ReadOnlySpan<ColorAttachmentDesc> Colors { get; }
    public DepthStencilAttachmentDesc? DepthStencil { get; }
    public uint Width { get; }
    public uint Height { get; }
    public RenderingOptions Options { get; }
}

public readonly record struct Viewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinimumDepth = 0,
    float MaximumDepth = 1);

public readonly record struct ScissorRect(int X, int Y, int Width, int Height);

public readonly record struct VertexBufferBinding(
    Buffer Buffer,
    ulong Offset,
    uint Stride,
    ulong Size);

public enum IndexType : byte
{
    UInt16,
    UInt32,
}

public readonly record struct IndexBufferBinding(
    Buffer Buffer,
    ulong Offset,
    ulong Size,
    IndexType Type);

public readonly record struct StreamOutputBufferBinding(
    Buffer Buffer,
    ulong Offset,
    ulong Size,
    Buffer? FilledSizeBuffer = null,
    ulong FilledSizeOffset = 0);

public enum PredicationOperation : byte
{
    EqualZero,
    NotEqualZero,
}

public readonly record struct DrawArguments(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance);

public readonly record struct DrawIndexedArguments(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance);

public readonly record struct DispatchArguments(uint X, uint Y, uint Z);
