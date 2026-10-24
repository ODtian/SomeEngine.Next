using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using SomeEngine.Graphics.Direct3D12;
using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics.Validation;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Static validation surface for borrowed D3D12 pointers and explicit
/// Borrowed/Transferred native imports. Getters never AddRef. Command-list retention input is consumed
/// synchronously, and recorded native work retains the resources it needs through completion.</para>
/// <para><b>After Dispose:</b> This type has no Dispose state; disposing the layer or owning object
/// invalidates every pointer and borrow obtained through it.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public static unsafe class D3D12ValidationNativeAccess
{
    public static ID3D12Device10* GetNativeDevice(
        this ValidationLayer layer,
        Device device)
    {
        layer.RequireNativeDevice(device);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeDevice(device);
    }

    public static IDXGIAdapter4* GetNativeAdapter(
        this ValidationLayer layer,
        Device device)
    {
        layer.RequireNativeDevice(device);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeAdapter(device);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer layer,
        Buffer buffer)
    {
        layer.RequireNativeResource(buffer);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeResource(buffer);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer layer,
        Texture texture)
    {
        layer.RequireNativeResource(texture);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeResource(texture);
    }

    public static ID3D12Heap* GetNativeHeap(
        this ValidationLayer layer,
        Heap heap)
    {
        layer.RequireNativeResource(heap);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeHeap(heap);
    }

    public static ID3D12PipelineState* GetNativePipelineState(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: false);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativePipelineState(pipeline);
    }

    public static ID3D12StateObject* GetNativeStateObject(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: true);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeStateObject(pipeline);
    }

    public static ID3D12RootSignature* GetNativeRootSignature(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativeResource(pipeline);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeRootSignature(pipeline);
    }

    public static ID3D12QueryHeap* GetNativeQueryHeap(
        this ValidationLayer layer,
        QueryPool pool)
    {
        layer.RequireNativeResource(pool);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeQueryHeap(pool);
    }

    public static ID3D12Fence* GetNativeTimeline(
        this ValidationLayer layer,
        ExternalTimeline timeline)
    {
        layer.RequireNativeResource(timeline);
        return layer.RequireNativeBackend<D3D12Backend>().GetNativeTimeline(timeline);
    }

    /// <summary>Imports a D3D12 Buffer pointer through the validated native-access boundary.</summary>
    public static Buffer ImportBuffer(
        this ValidationLayer layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        D3D12Backend backend = layer.RequireNativeBackend<D3D12Backend>();
        nint address = (nint)resource;
        BufferDesc description = desc;
        ImportedResourceState importedState = state;
        return layer.ImportNativeBuffer(device, () => backend.ImportBuffer(
            device, (ID3D12Resource*)address, ownership, description, importedState));
    }

    /// <summary>Imports a D3D12 Texture pointer through the validated native-access boundary.</summary>
    public static Texture ImportTexture(
        this ValidationLayer layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        D3D12Backend backend = layer.RequireNativeBackend<D3D12Backend>();
        nint address = (nint)resource;
        TextureDesc description = desc;
        ImportedResourceState importedState = state;
        return layer.ImportNativeTexture(device, description, value => backend.ImportTexture(
            device, (ID3D12Resource*)address, ownership, value, importedState));
    }

    /// <summary>Imports a D3D12 Heap pointer through the validated native-access boundary.</summary>
    public static Heap ImportHeap(
        this ValidationLayer layer,
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc)
    {
        D3D12Backend backend = layer.RequireNativeBackend<D3D12Backend>();
        nint address = (nint)heap;
        HeapDesc description = desc;
        return layer.ImportNativeHeap(device, description, () => backend.ImportHeap(
            device, (ID3D12Heap*)address, ownership, description));
    }

    public static D3D12CommandListBorrow BorrowCommandList(
        this ValidationLayer layer,
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources)
    {
        D3D12Backend backend = layer.RequireNativeBackend<D3D12Backend>();
        layer.ValidateNativeCommands(context, retainedResources);
        D3D12CommandListBorrow result = backend.BorrowCommandList(context, retainedResources);
        layer.RecordNativeCommands(context, retainedResources);
        return result;
    }

    public static D3D12CommandQueueLock LockCommandQueue(
        this ValidationLayer layer,
        Queue queue) =>
        QueueAccess.GetValue(layer, static value => new QueueValidation(value)).LockNativeCommandQueue(queue);
    private static readonly ConditionalWeakTable<ValidationLayer, QueueValidation> QueueAccess = new();

    private sealed class QueueValidation(ValidationLayer layer)
    {
        private readonly ValidationLayer _layer = layer;
        private readonly object _gate = new();
        private readonly ConditionalWeakTable<Queue, NativeQueueLockValidationState> _nativeQueueLockStates = new();
        private sealed class NativeQueueLockValidationState
        {
            internal int ThreadId;
            internal QueueBorrow? Lease;
        }
        internal D3D12CommandQueueLock LockNativeCommandQueue(Queue queue)
        {
            D3D12Backend backend = _layer.RequireNativeBackend<D3D12Backend>();

            _layer.RequireNativeQueue(queue);
            int threadId = Environment.CurrentManagedThreadId;
            NativeQueueLockValidationState state;
            lock (_gate)
            {
                if (!_nativeQueueLockStates.TryGetValue(queue, out state!))
                {
                    state = new NativeQueueLockValidationState();
                    state.Lease = new QueueBorrow(this, queue, state);
                    _nativeQueueLockStates.Add(queue, state);
                }
                if (state.ThreadId == threadId)
                {
                    _layer.RejectNativeAccess(
                        "Concurrency",
                        "The current thread already holds this Queue's native lock.");
                }
            }

            state.Lease!.WaitUntilReleased();
            D3D12CommandQueueLock inner = backend.LockCommandQueue(queue);
            D3D12CommandQueueLockLease innerLease = inner.Lease;
            ulong sequence = inner.Sequence;
            QueueBorrow lease = state.Lease!;
            lease.WaitUntilReleased();
            lease.Publish(innerLease, sequence, threadId);
            return new D3D12CommandQueueLock(lease, sequence);
        }

        private sealed unsafe class QueueBorrow : D3D12CommandQueueLockLease
        {
            private readonly QueueValidation _owner;
            private readonly Queue _queue;
            private readonly NativeQueueLockValidationState _state;
            private D3D12CommandQueueLockLease? _inner;
            private ulong _innerSequence;

            internal QueueBorrow(
                QueueValidation owner,
                Queue queue,
                NativeQueueLockValidationState state)
            {
                _owner = owner;
                _queue = queue;
                _state = state;
            }

            internal void Publish(
                D3D12CommandQueueLockLease inner,
                ulong sequence,
                int threadId)
            {
                _inner = inner;
                _innerSequence = sequence;
                try
                {
                    lock (_owner._gate)
                        _state.ThreadId = threadId;
                    Activate(sequence);
                }
                catch
                {
                    Abort();
                    throw;
                }
            }

            protected override bool IsHeldCore(ulong sequence) =>
                _inner?.IsHeld(sequence) == true;

            protected override ID3D12CommandQueue* GetPointerCore() =>
                _inner!.GetPointer(_innerSequence);

            protected override void ReleaseCore()
            {
                lock (_owner._gate)
                {
                    if (_owner._nativeQueueLockStates.TryGetValue(_queue, out NativeQueueLockValidationState? state) &&
                        ReferenceEquals(state, _state) &&
                        state.ThreadId != 0)
                    {
                        state.ThreadId = 0;
                    }
                }
                D3D12CommandQueueLockLease? inner = _inner;
                ulong sequence = _innerSequence;
                _inner = null;
                _innerSequence = 0;
                inner?.Release(sequence);
            }

            private void Abort()
            {
                lock (_owner._gate)
                {
                    if (_owner._nativeQueueLockStates.TryGetValue(_queue, out NativeQueueLockValidationState? state) &&
                        ReferenceEquals(state, _state))
                    {
                        state.ThreadId = 0;
                    }
                }
                D3D12CommandQueueLockLease? inner = _inner;
                ulong sequence = _innerSequence;
                _inner = null;
                _innerSequence = 0;
                inner?.Release(sequence);
            }
        }
    }
}
