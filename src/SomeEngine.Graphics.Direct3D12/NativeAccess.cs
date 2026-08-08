using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>Reports that the Device exposes the pinned Direct3D 12 native-access surface.</summary>
public sealed class D3D12NativeAccess : DeviceCapability
{
    internal D3D12NativeAccess(Device device)
        : base(device)
    {
    }
}

/// <summary>Reports the Direct3D 12 diagnostic facilities selected before Device creation.</summary>
public sealed class D3D12Diagnostics : DeviceCapability
{
    internal D3D12Diagnostics(
        Device device,
        bool debugLayerEnabled,
        bool gpuBasedValidationEnabled,
        bool synchronizedQueueValidationEnabled,
        bool dredEnabled)
        : base(device)
    {
        DebugLayerEnabled = debugLayerEnabled;
        GpuBasedValidationEnabled = gpuBasedValidationEnabled;
        SynchronizedQueueValidationEnabled = synchronizedQueueValidationEnabled;
        DredEnabled = dredEnabled;
    }

    public bool DebugLayerEnabled { get; }
    public bool GpuBasedValidationEnabled { get; }
    public bool SynchronizedQueueValidationEnabled { get; }
    public bool DredEnabled { get; }

    /// <summary>Returns the retained terminal diagnostic while the Device is Lost.</summary>
    public GraphicsException? DeviceLoss => Device.Status == DeviceStatus.Lost
        ? Device.Loss
        : null;

    /// <summary>Returns native setter counts retained by one command payload.</summary>
    public D3D12CommandStatistics GetCommandStatistics(in RecordedCommands commands) =>
        D3D12Backend.GetCommandStatistics(Device, commands);
}

public readonly record struct D3D12CommandStatistics(
    int PipelineSetters,
    int PersistentBindingSetters,
    int ViewportSetters,
    int ScissorSetters);

/// <summary>
/// A stack-only exclusive borrow of the native command Queue. Every copy shares one release state.
/// </summary>
public readonly unsafe ref struct D3D12CommandQueueLock
{
    private readonly D3D12CommandQueueLockLease? _lease;
    private readonly ulong _sequence;

    internal D3D12CommandQueueLock(D3D12CommandQueueLockLease lease, ulong sequence)
    {
        _lease = lease;
        _sequence = sequence;
    }

    public bool IsHeld => _lease?.IsHeld(_sequence) == true;

    public ID3D12CommandQueue* Pointer => _lease is null
        ? throw new InvalidOperationException("The default D3D12CommandQueueLock is not held.")
        : _lease.GetPointer(_sequence);

    public void Dispose() => _lease?.Release(_sequence);
}

internal unsafe abstract class D3D12CommandQueueLockLease
{
    internal abstract bool IsHeld(ulong sequence);
    internal abstract ID3D12CommandQueue* GetPointer(ulong sequence);
    internal abstract void Release(ulong sequence);
}

public sealed unsafe partial class D3D12Backend
{
    internal static D3D12CommandStatistics GetCommandStatistics(
        Device device,
        in RecordedCommands commands)
    {
        if (!ReferenceEquals(commands.Device, device))
            throw new ArgumentException("The command payload belongs to another Device.", nameof(commands));
        if (commands.Lease is not D3D12RecordedCommandsLease native)
            throw new ArgumentException("The command payload is not owned by D3D12.", nameof(commands));
        return native.GetStatistics(commands.Sequence);
    }

    public D3D12CommandQueueLock LockCommandQueue(Queue queue) =>
        NativeCast.Queue(queue).AcquireNativeLock();

    private sealed class D3D12NativeQueueLockLease : D3D12CommandQueueLockLease
    {
        private readonly D3D12Queue _queue;
        private readonly ulong _sequence;
        private int _held = 1;

        internal D3D12NativeQueueLockLease(D3D12Queue queue, ulong sequence)
        {
            _queue = queue;
            _sequence = sequence;
        }

        internal override bool IsHeld(ulong sequence) =>
            sequence == _sequence && Volatile.Read(ref _held) != 0;

        internal override ID3D12CommandQueue* GetPointer(ulong sequence)
        {
            if (!IsHeld(sequence))
                throw new InvalidOperationException("The D3D12 command Queue lock is no longer held.");
            _queue.NativeDevice.ThrowIfUnavailable();
            return _queue.Native is null
                ? throw new ObjectDisposedException(nameof(Queue))
                : _queue.Native;
        }

        internal override void Release(ulong sequence)
        {
            if (sequence != _sequence || Interlocked.Exchange(ref _held, 0) == 0)
                return;
            Monitor.Exit(_queue.Gate);
        }
    }

    private sealed partial class D3D12Queue
    {
        private ulong _nextNativeLockSequence = 1;

        internal D3D12CommandQueueLock AcquireNativeLock()
        {
            Monitor.Enter(Gate);
            try
            {
                _device.ThrowIfUnavailable();
                if (_nextNativeLockSequence == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The D3D12 command Queue lock sequence domain is exhausted.");
                }

                ulong sequence = _nextNativeLockSequence;
                D3D12NativeQueueLockLease lease = new(this, sequence);
                _nextNativeLockSequence = sequence + 1;
                return new D3D12CommandQueueLock(lease, sequence);
            }
            catch
            {
                Monitor.Exit(Gate);
                throw;
            }
        }
    }
}
