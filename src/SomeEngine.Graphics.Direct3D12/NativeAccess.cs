using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>Reports that the Device exposes the pinned Direct3D 12 native-access surface.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed Device-owned capability marker; callers never Dispose it. Native
/// getters on <see cref="D3D12Backend"/> return borrowed pointers and never AddRef them.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state. Device disposal invalidates
/// every borrowed pointer and native operation.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class D3D12NativeAccess : DeviceCapability
{
    internal D3D12NativeAccess(Device device)
        : base(device)
    {
    }
}

/// <summary>Reports the Direct3D 12 diagnostic facilities selected before Device creation.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

    /// <summary>Returns native setter counts retained by one reusable command bundle.</summary>
    public D3D12CommandStatistics GetCommandStatistics(RecordedBundle bundle) =>
        D3D12Backend.GetCommandStatistics(Device, bundle);
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12CommandStatistics(
    int PipelineSetters,
    int PersistentBindingSetters,
    int ViewportSetters,
    int ScissorSetters,
    D3D12StateSetterStatistics StateSetters = default);

/// <summary>Native-call counts for the complete normalized future-state setter set.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12StateSetterStatistics(
    int Pipelines,
    int PersistentParameterBindings,
    int TransientParameterBindings,
    int VertexBuffers,
    int IndexBuffers,
    int StreamOutputBuffers,
    int Viewports,
    int Scissors,
    int BlendConstants,
    int StencilReferences,
    int DepthBounds,
    int DepthBias,
    int PrimitiveTopologies,
    int StripCuts,
    int Predication,
    int ShadingRates,
    int ShadingRateImages,
    int WorkGraphPrograms);

/// <summary>
/// A stack-only exclusive borrow of the native command Queue. Every copy shares one release state.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed exclusive borrow of the Queue native object; it transfers no COM ownership and every copy shares one release state.</para>
/// <para><b>After Dispose:</b> The native Queue borrow is invalid and Dispose is a no-op.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <summary>
/// A stack-only borrow of the active native command list. It is valid only until the next public
/// operation on the same CommandContext.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Borrowed active native command list; it transfers no COM ownership.</para>
/// <para><b>After Dispose:</b> This value has no Dispose operation; the borrow expires at the next public operation on its CommandContext.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly unsafe ref struct D3D12CommandListBorrow
{
    private readonly D3D12CommandListBorrowLease? _lease;
    private readonly ulong _sequence;

    internal D3D12CommandListBorrow(D3D12CommandListBorrowLease lease, ulong sequence)
    {
        _lease = lease;
        _sequence = sequence;
    }

    public bool IsValid => _lease?.IsValid(_sequence) == true;

    public ID3D12GraphicsCommandList10* Pointer => _lease is null
        ? throw new InvalidOperationException("The default D3D12CommandListBorrow is not valid.")
        : _lease.GetPointer(_sequence);
}

internal unsafe abstract class D3D12CommandListBorrowLease
{
    internal abstract bool IsValid(ulong sequence);
    internal abstract ID3D12GraphicsCommandList10* GetPointer(ulong sequence);
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

    internal static D3D12CommandStatistics GetCommandStatistics(
        Device device,
        RecordedBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!ReferenceEquals(bundle.Device, device))
            throw new ArgumentException("The command bundle belongs to another Device.", nameof(bundle));
        return NativeCast.Bundle(bundle).Statistics;
    }

    /// <summary>Returns a caller-disposed exclusive borrow of the native Queue.</summary>
    /// <remarks>The borrow holds the same Queue gate as submission and sparse mapping.</remarks>
    public D3D12CommandQueueLock LockCommandQueue(Queue queue) =>
        NativeCast.Queue(queue).AcquireNativeLock();

    internal bool IsCommandQueueLockHeldByCurrentThread(Queue queue) =>
        Monitor.IsEntered(NativeCast.Queue(queue).Gate);

    /// <summary>Returns the borrowed native Device pointer without AddRef.</summary>
    public ID3D12Device10* GetNativeDevice(Device device) =>
        NativeCast.Device(device).Native;

    /// <summary>Returns the borrowed native Adapter pointer without AddRef.</summary>
    public IDXGIAdapter4* GetNativeAdapter(Device device) =>
        NativeCast.Device(device).NativeAdapter;

    /// <summary>Returns the borrowed native Buffer resource pointer without AddRef.</summary>
    public ID3D12Resource* GetNativeResource(Buffer buffer) =>
        NativeCast.Buffer(buffer).Native;

    /// <summary>Returns the borrowed native Texture resource pointer without AddRef.</summary>
    public ID3D12Resource* GetNativeResource(Texture texture) =>
        NativeCast.Texture(texture).Native;

    /// <summary>Returns the borrowed native Heap pointer without AddRef.</summary>
    public ID3D12Heap* GetNativeHeap(Heap heap) =>
        NativeCast.Heap(heap).Native;

    /// <summary>Returns the borrowed native graphics/compute/mesh pipeline pointer without AddRef.</summary>
    public ID3D12PipelineState* GetNativePipelineState(Pipeline pipeline) =>
        (ID3D12PipelineState*)NativeCast.Pipeline(pipeline).NativeObject;

    /// <summary>Returns the borrowed native ray-tracing/Work-Graph state object without AddRef.</summary>
    public ID3D12StateObject* GetNativeStateObject(Pipeline pipeline) =>
        (ID3D12StateObject*)NativeCast.Pipeline(pipeline).NativeObject;

    /// <summary>Returns the borrowed native root signature without AddRef.</summary>
    public ID3D12RootSignature* GetNativeRootSignature(Pipeline pipeline) =>
        NativeCast.Pipeline(pipeline).RootLayout.Native;

    /// <summary>Returns the borrowed native Query Heap without AddRef.</summary>
    public ID3D12QueryHeap* GetNativeQueryHeap(QueryPool pool) =>
        NativeCast.QueryPool(pool).Native;

    /// <summary>Returns the borrowed native Fence for an ExternalTimeline without AddRef.</summary>
    /// <remarks>A Queue's private completion Fence is never exposed by native access.</remarks>
    public ID3D12Fence* GetNativeTimeline(ExternalTimeline timeline) =>
        NativeCast.Timeline(timeline).Native;

    /// <summary>Returns a Recording-scoped borrowed command list and dirties its full state shadow.</summary>
    /// <remarks>
    /// The resource span is consumed synchronously. Automatic retirement captures every listed
    /// resource; Manual retirement captures none and requires the caller to retain every native use.
    /// </remarks>
    public D3D12CommandListBorrow BorrowCommandList(
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        return command.BorrowNativeCommandList(retainedResources);
    }

    private sealed class D3D12NativeCommandListBorrowLease : D3D12CommandListBorrowLease
    {
        private readonly D3D12CommandContext _context;

        internal D3D12NativeCommandListBorrowLease(D3D12CommandContext context) =>
            _context = context;

        internal override bool IsValid(ulong sequence) =>
            _context.IsNativeCommandListBorrowValid(sequence);

        internal override ID3D12GraphicsCommandList10* GetPointer(ulong sequence) =>
            _context.GetNativeCommandListBorrowPointer(sequence);
    }

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

    private sealed partial class D3D12CommandContext
    {
        private ulong _nativeAccessSequence = 1;
        private ulong _activeNativeCommandListBorrow;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void BeginPublicCall()
        {
            if (_activeNativeCommandListBorrow == 0)
                return;
            if (_nativeAccessSequence == ulong.MaxValue)
                ThrowNativeAccessSequenceExhausted();

            _activeNativeCommandListBorrow = 0;
            _nativeAccessSequence++;
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ThrowNativeAccessSequenceExhausted() =>
            throw new InvalidOperationException(
                "The native command-list borrow sequence domain is exhausted.");

        internal D3D12CommandListBorrow BorrowNativeCommandList(
            ReadOnlySpan<Resource> retainedResources)
        {
            foreach (Resource resource in retainedResources)
            {
                switch (resource)
                {
                    case Buffer buffer:
                        Capture(NativeCast.Buffer(buffer));
                        break;
                    case Texture texture:
                        Capture(NativeCast.Texture(texture));
                        break;
                    default:
                        throw new ArgumentException(
                            "The native command-list retention list contains an unknown Resource type.",
                            nameof(retainedResources));
                }
            }

            InvalidateStateShadow();
            ulong sequence = _nativeAccessSequence;
            _activeNativeCommandListBorrow = sequence;
            return new D3D12CommandListBorrow(
                new D3D12NativeCommandListBorrowLease(this),
                sequence);
        }

        internal bool IsNativeCommandListBorrowValid(ulong sequence) =>
            sequence != 0 &&
            Volatile.Read(ref _activeNativeCommandListBorrow) == sequence &&
            List is not null;

        internal ID3D12GraphicsCommandList10* GetNativeCommandListBorrowPointer(ulong sequence)
        {
            if (!IsNativeCommandListBorrowValid(sequence))
            {
                throw new InvalidOperationException(
                    "The D3D12 command-list borrow is no longer valid.");
            }

            _device.ThrowIfUnavailable();
            return List;
        }
    }
}
