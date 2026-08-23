using System.Numerics;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct CommandContextDesc(
    QueueType QueueType,
    uint QueueIndex,
    uint InitialSlotCount,
    bool Bundle = false,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct CommandRecordingDesc(
    uint InitialResourceDescriptorCapacity = 0,
    uint InitialSamplerDescriptorCapacity = 0,
    uint InitialCapturedResourceCapacity = 0,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class CommandContext : DeviceResource
{
    internal CommandContext(
        Device device,
        QueueType queueType,
        uint queueIndex,
        bool bundle,
        string? label)
        : base(device, label)
    {
        QueueType = queueType;
        QueueIndex = queueIndex;
        Bundle = bundle;
    }

    public QueueType QueueType { get; }
    public uint QueueIndex { get; }
    public bool Bundle { get; }
}

internal abstract class RecordedCommandsLease
{
    private readonly object _gate = new();
    private ulong _sequence;
    private RecordedCommandsStatus _status = RecordedCommandsStatus.Discarded;
    private bool _callerDisposed = true;
    private int _callerReleaseState = 2;
    private int _callerReleaseOwnerThreadId;

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
            while (_callerReleaseState == 1)
                Monitor.Wait(_gate);
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
            _callerReleaseState = 0;
            _callerReleaseOwnerThreadId = 0;
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
            if (_callerReleaseState == 1)
            {
                if (_callerReleaseOwnerThreadId == Environment.CurrentManagedThreadId)
                    return false;
                while (_sequence == sequence && _callerReleaseState == 1)
                    Monitor.Wait(_gate);
                return false;
            }
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
            if (_sequence != sequence)
                return;
            if (_callerDisposed)
            {
                if (_callerReleaseState == 1 &&
                    _callerReleaseOwnerThreadId == Environment.CurrentManagedThreadId)
                {
                    return;
                }
                while (_sequence == sequence && _callerReleaseState == 1)
                    Monitor.Wait(_gate);
                return;
            }
            _callerDisposed = true;
            if (_status == RecordedCommandsStatus.Executable)
            {
                _status = RecordedCommandsStatus.Discarded;
                _callerReleaseState = 1;
                _callerReleaseOwnerThreadId = Environment.CurrentManagedThreadId;
                discard = true;
            }
            else
            {
                _callerReleaseState = 2;
                _callerReleaseOwnerThreadId = 0;
            }
        }
        if (!discard)
            return;
        try
        {
            DiscardUnsubmitted(sequence);
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                if (_sequence == sequence)
                {
                    _callerReleaseState = 2;
                    _callerReleaseOwnerThreadId = 0;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    protected void CancelActivation(ulong sequence)
    {
        lock (_gate)
        {
            EnsureSequenceUnderGate(sequence);
            if (_status != RecordedCommandsStatus.Executable)
                return;
            _status = RecordedCommandsStatus.Discarded;
            _callerDisposed = true;
            _callerReleaseState = 2;
            _callerReleaseOwnerThreadId = 0;
        }
    }

    protected abstract void DiscardUnsubmitted(ulong sequence);

    private void EnsureSequenceUnderGate(ulong sequence)
    {
        if (sequence == 0 || _sequence != sequence)
            throw new InvalidOperationException("The RecordedCommands sequence is no longer current.");
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed single-submit right. The accepted submission retains its native execution dependencies until Queue completion; public wrappers remain caller-owned.</para>
/// <para><b>After Dispose:</b> Status remains readable as Disposed; submission and payload access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class RecordedBundle : DeviceResource
{
    internal RecordedBundle(Device device, string? label)
        : base(device, label)
    {
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct MemoryBarrier(
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter,
    BarrierPhase Phase = BarrierPhase.Complete);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct BufferBarrier(
    Buffer Buffer,
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter,
    BarrierPhase Phase = BarrierPhase.Complete);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct TextureBarrier(
    Texture Texture,
    TextureSubresourceRange Range,
    PipelineSync SyncBefore,
    PipelineSync SyncAfter,
    ResourceAccess AccessBefore,
    ResourceAccess AccessAfter,
    TextureLayout LayoutBefore,
    TextureLayout LayoutAfter,
    BarrierPhase Phase = BarrierPhase.Complete);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AliasingResource(
    Resource Resource,
    TextureSubresourceRange? TextureRange = null);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct QueueRelease(
    Resource Resource,
    TextureSubresourceRange? TextureRange,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout,
    QueueType DestinationQueueType);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct QueueAcquire(
    Resource Resource,
    TextureSubresourceRange? TextureRange,
    QueueType SourceQueueType,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout? Layout);

/// <summary>
/// Groups independent barriers that occur at one command-stream boundary.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>Barriers in one batch must not depend on another barrier in the same batch; use separate calls when ordering is required.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct BarrierBatch
{
    public BarrierBatch(
        ReadOnlySpan<MemoryBarrier> memoryBarriers,
        ReadOnlySpan<QueueAcquire> queueAcquires,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<QueueRelease> queueReleases)
    {
        MemoryBarriers = memoryBarriers;
        QueueAcquires = queueAcquires;
        BufferBarriers = bufferBarriers;
        TextureBarriers = textureBarriers;
        QueueReleases = queueReleases;
    }

    public ReadOnlySpan<MemoryBarrier> MemoryBarriers { get; }
    public ReadOnlySpan<QueueAcquire> QueueAcquires { get; }
    public ReadOnlySpan<BufferBarrier> BufferBarriers { get; }
    public ReadOnlySpan<TextureBarrier> TextureBarriers { get; }
    public ReadOnlySpan<QueueRelease> QueueReleases { get; }
    public bool IsEmpty =>
        MemoryBarriers.IsEmpty &&
        QueueAcquires.IsEmpty &&
        BufferBarriers.IsEmpty &&
        TextureBarriers.IsEmpty &&
        QueueReleases.IsEmpty;
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct BufferCopy(
    Buffer Source,
    ulong SourceOffset,
    Buffer Destination,
    ulong DestinationOffset,
    ulong Size);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ResolveType : byte
{
    Average,
    Minimum,
    Maximum,
    SampleZero,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct TextureResolve(
    Texture Source,
    uint SourceMipLevel,
    uint SourceArrayLayer,
    Texture Destination,
    uint DestinationMipLevel,
    uint DestinationArrayLayer,
    Format Format,
    ResolveType Type = ResolveType.Average);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum LoadType : byte
{
    Load,
    Clear,
    Discard,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum StoreType : byte
{
    Store,
    Discard,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct ColorAttachmentDesc(
    ColorAttachmentView View,
    LoadType Load,
    StoreType Store,
    Vector4 ClearValue,
    ColorAttachmentView? ResolveView = null,
    ResolveType ResolveType = ResolveType.Average);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DepthStencilAttachmentDesc(
    DepthStencilView View,
    LoadType DepthLoad,
    StoreType DepthStore,
    LoadType StencilLoad,
    StoreType StencilStore,
    float ClearDepth = 1,
    byte ClearStencil = 0);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum RenderingOptions : byte
{
    None = 0,
    AllowUnorderedAccessWrites = 1 << 0,
    Suspending = 1 << 1,
    Resuming = 1 << 2,
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct Viewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinimumDepth = 0,
    float MaximumDepth = 1);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct ScissorRect(int X, int Y, int Width, int Height);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct VertexBufferBinding(
    Buffer Buffer,
    ulong Offset,
    uint Stride,
    ulong Size);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum IndexType : byte
{
    UInt16,
    UInt32,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct IndexBufferBinding(
    Buffer Buffer,
    ulong Offset,
    ulong Size,
    IndexType Type);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct StreamOutputBufferBinding(
    Buffer Buffer,
    ulong Offset,
    ulong Size,
    Buffer? FilledSizeBuffer = null,
    ulong FilledSizeOffset = 0);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum PredicationOperation : byte
{
    EqualZero,
    NotEqualZero,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DrawArguments(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DrawIndexedArguments(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DispatchArguments(uint X, uint Y, uint Z);
