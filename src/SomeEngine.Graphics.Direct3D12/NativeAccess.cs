using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed class QueueExclusion
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal Scope EnterScope()
    {
        Enter();
        return new Scope(this);
    }

    internal void Enter()
        => _semaphore.Wait();

    internal void Exit()
        => _semaphore.Release();

    internal readonly struct Scope : IDisposable
    {
        private readonly QueueExclusion _owner;

        internal Scope(QueueExclusion owner) => _owner = owner;

        public void Dispose() => _owner.Exit();
    }
}

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

    /// <summary>Returns the current private resource-pool telemetry.</summary>
    public D3D12MemoryAllocatorInfo MemoryAllocator =>
        D3D12Backend.GetMemoryAllocatorInfo(Device);

    /// <summary>Returns asynchronous Pipeline creation telemetry.</summary>
    public D3D12PipelineCreationInfo PipelineCreation =>
        D3D12Backend.GetPipelineCreationInfo(Device);

    /// <summary>Returns the retained structured DRED report after Device loss.</summary>
    public D3D12DeviceLossReport? DeviceLossReport =>
        D3D12Backend.GetDeviceLossReport(Device);

    /// <summary>Returns one thread-safe DXGI presentation snapshot for this Device.</summary>
    public D3D12PresentationInfo GetPresentationInfo(Swapchain swapchain) =>
        D3D12Backend.GetPresentationInfo(Device, swapchain);

    /// <summary>Returns the retained terminal diagnostic while the Device is Lost.</summary>
    public GraphicsException? DeviceLoss => Device.Status == DeviceStatus.Lost
        ? Device.Loss
        : null;

    /// <summary>
    /// Returns the first Device-teardown failure. This cold diagnostic remains readable after
    /// Device disposal and is never reported as native Device loss.
    /// </summary>
    public Exception? TeardownFailure => Device.TeardownFailure;

}

/// <summary>
/// A stack-only exclusive borrow of the native command Queue. Every copy shares one release state.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
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

    internal D3D12CommandQueueLockLease Lease => _lease ??
        throw new InvalidOperationException("The default D3D12CommandQueueLock is not held.");

    internal ulong Sequence => _sequence;
}

/// <summary>
/// A stack-only borrow of the active native command list. It is valid only until the next public
/// operation on the same CommandContext.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
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
    private const int Released = 0;
    private const int Held = 1;
    private const int Invalidated = 2;
    private const int Releasing = 3;

    private ulong _sequence;
    private int _state;

    internal bool IsHeld(ulong sequence) =>
        sequence == Volatile.Read(ref _sequence) &&
        Volatile.Read(ref _state) == Held &&
        IsHeldCore(sequence);

    internal ID3D12CommandQueue* GetPointer(ulong sequence)
    {
        if (!IsHeld(sequence))
            throw new InvalidOperationException("The D3D12 command Queue lock is no longer held.");
        return GetPointerCore();
    }

    internal void Release(ulong sequence)
    {
        if (sequence != Volatile.Read(ref _sequence))
            return;
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state == Released)
                return;
            if (state == Releasing)
            {
                var spinner = new SpinWait();
                while (sequence == Volatile.Read(ref _sequence) &&
                       Volatile.Read(ref _state) == Releasing)
                    spinner.SpinOnce();
                return;
            }
            if (Interlocked.CompareExchange(ref _state, Releasing, state) == state)
                break;
        }
        try
        {
            ReleaseCore();
        }
        catch
        {
        }
        finally
        {
            Volatile.Write(ref _state, Released);
        }
    }

    internal void Activate(ulong sequence)
    {
        if (sequence == 0 || Volatile.Read(ref _state) != Released)
            throw new InvalidOperationException("The D3D12 command Queue lock authority is still active.");
        Volatile.Write(ref _sequence, sequence);
        Volatile.Write(ref _state, Held);
    }

    internal void WaitUntilReleased()
    {
        var spinner = new SpinWait();
        while (Volatile.Read(ref _state) != Released)
            spinner.SpinOnce();
    }

    internal void Invalidate(ulong sequence)
    {
        if (sequence == Volatile.Read(ref _sequence))
            _ = Interlocked.CompareExchange(ref _state, Invalidated, Held);
    }

    protected virtual bool IsHeldCore(ulong sequence) => true;
    protected abstract ID3D12CommandQueue* GetPointerCore();
    protected abstract void ReleaseCore();
}

internal sealed unsafe partial class D3D12Backend
{
    /// <summary>Returns a caller-disposed exclusive borrow of the native Queue.</summary>
    /// <remarks>The borrow holds the same Queue gate as submission and sparse mapping.</remarks>
    public D3D12CommandQueueLock LockCommandQueue(Queue queue) =>
        RequireQueue(queue, nameof(queue)).AcquireNativeLock();

    /// <summary>Returns the borrowed native Device pointer without AddRef.</summary>
    public ID3D12Device10* GetNativeDevice(Device device) =>
        RequireDevice(device, nameof(device)).Native;

    /// <summary>Returns the borrowed native Adapter pointer without AddRef.</summary>
    public IDXGIAdapter4* GetNativeAdapter(Device device) =>
        RequireDevice(device, nameof(device)).NativeAdapter;

    /// <summary>Returns the borrowed native Buffer resource pointer without AddRef.</summary>
    public ID3D12Resource* GetNativeResource(Buffer buffer) =>
        RequireBuffer(buffer).Native;

    /// <summary>Returns the borrowed native Texture resource pointer without AddRef.</summary>
    public ID3D12Resource* GetNativeResource(Texture texture) =>
        RequireTexture(texture).Native;

    /// <summary>Returns the borrowed native Heap pointer without AddRef.</summary>
    public ID3D12Heap* GetNativeHeap(Heap heap) =>
        RequireHeap(heap).Native;

    /// <summary>Returns the borrowed native graphics/compute/mesh pipeline pointer without AddRef.</summary>
    public ID3D12PipelineState* GetNativePipelineState(Pipeline pipeline) =>
        (ID3D12PipelineState*)RequirePipeline(pipeline).NativeObject;

    /// <summary>Returns the borrowed native ray-tracing/Work-Graph state object without AddRef.</summary>
    public ID3D12StateObject* GetNativeStateObject(Pipeline pipeline) =>
        (ID3D12StateObject*)RequirePipeline(pipeline).NativeObject;

    /// <summary>Returns the borrowed native root signature without AddRef.</summary>
    public ID3D12RootSignature* GetNativeRootSignature(Pipeline pipeline) =>
        RequirePipeline(pipeline).RootSignature.Native;

    /// <summary>Returns the borrowed native Query Heap without AddRef.</summary>
    public ID3D12QueryHeap* GetNativeQueryHeap(QueryPool pool) =>
        RequireQueryPool(pool).Native;

    /// <summary>Returns the borrowed native Fence for an ExternalTimeline without AddRef.</summary>
    /// <remarks>A Queue's private completion Fence is never exposed by native access.</remarks>
    public ID3D12Fence* GetNativeTimeline(ExternalTimeline timeline) =>
        RequireTimeline(timeline).Native;

    /// <summary>Returns a Recording-scoped borrowed command list and dirties its full state shadow.</summary>
    /// <remarks>
    /// The resource span is consumed synchronously. Automatic retirement captures every listed
    /// resource; Manual retirement captures none and requires the caller to retain every native use.
    /// </remarks>
    public D3D12CommandListBorrow BorrowCommandList(
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
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

        internal D3D12NativeQueueLockLease(D3D12Queue queue) => _queue = queue;

        protected override ID3D12CommandQueue* GetPointerCore()
        {
            _queue.NativeDevice.ThrowIfUnavailable();
            return _queue.Native is null
                ? throw new ObjectDisposedException(nameof(Queue))
                : _queue.Native;
        }

        protected override void ReleaseCore() => _queue.Gate.Exit();
    }

    private sealed partial class D3D12Queue
    {
        private ulong _nextNativeLockSequence = 1;
        private readonly D3D12NativeQueueLockLease _nativeLock;

        internal D3D12CommandQueueLock AcquireNativeLock()
        {
            Gate.Enter();
            try
            {
                _nativeLock.WaitUntilReleased();
                _device.ThrowIfUnavailable();
                if (_nextNativeLockSequence == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The D3D12 command Queue lock sequence domain is exhausted.");
                }

                ulong sequence = _nextNativeLockSequence;
                _nativeLock.Activate(sequence);
                _nextNativeLockSequence = sequence + 1;
                return new D3D12CommandQueueLock(_nativeLock, sequence);
            }
            catch
            {
                Gate.Exit();
                throw;
            }
        }

        internal void InvalidateNativeLock() =>
            _nativeLock.Invalidate(_nextNativeLockSequence - 1);
    }

    private sealed partial class D3D12CommandContext
    {
        private ulong _nativeAccessSequence = 1;
        private ulong _activeNativeCommandListBorrow;
        private object[] _resolvedEncodeResources = [];
        private readonly D3D12NativeCommandListBorrowLease _nativeCommandListBorrowLease;

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
            int resourceCount = retainedResources.Length;
            PrepareCaptures(resourceCount, 0, resourceCount);
            PrepareSwapchainUses(resourceCount);
            PrepareResolvedResources(resourceCount);
            int resolvedCount = 0;
            try
            {
                for (int index = 0; index < resourceCount; index++)
                {
                    _resolvedEncodeResources[index] = retainedResources[index] switch
                    {
                        Buffer buffer => RequireD3D12.Buffer(buffer),
                        Texture texture => RequireD3D12.Texture(texture),
                        _ => throw new ArgumentException(
                            "The native command-list retention list contains an unknown Resource type.",
                            nameof(retainedResources)),
                    };
                    resolvedCount++;
                }

                for (int index = 0; index < resourceCount; index++)
                {
                    switch (_resolvedEncodeResources[index])
                    {
                        case D3D12Buffer buffer: Capture(buffer); break;
                        case D3D12TextureResource texture: Capture(texture); break;
                    }
                }

                InvalidateStateShadow();
                ulong sequence = _nativeAccessSequence;
                _activeNativeCommandListBorrow = sequence;
                return new D3D12CommandListBorrow(
                    _nativeCommandListBorrowLease,
                    sequence);
            }
            finally
            {
                Array.Clear(_resolvedEncodeResources, 0, resolvedCount);
            }
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
