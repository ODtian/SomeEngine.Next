using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using SomeEngine.Graphics.Direct3D12;

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
        return layer.RequireD3D12Backend().GetNativeDevice(device);
    }

    public static IDXGIAdapter4* GetNativeAdapter(
        this ValidationLayer layer,
        Device device)
    {
        layer.RequireNativeDevice(device);
        return layer.RequireD3D12Backend().GetNativeAdapter(device);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer layer,
        Buffer buffer)
    {
        layer.RequireNativeResource(buffer);
        return layer.RequireD3D12Backend().GetNativeResource(buffer);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer layer,
        Texture texture)
    {
        layer.RequireNativeResource(texture);
        return layer.RequireD3D12Backend().GetNativeResource(texture);
    }

    public static ID3D12Heap* GetNativeHeap(
        this ValidationLayer layer,
        Heap heap)
    {
        layer.RequireNativeResource(heap);
        return layer.RequireD3D12Backend().GetNativeHeap(heap);
    }

    public static ID3D12PipelineState* GetNativePipelineState(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: false);
        return layer.RequireD3D12Backend().GetNativePipelineState(pipeline);
    }

    public static ID3D12StateObject* GetNativeStateObject(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: true);
        return layer.RequireD3D12Backend().GetNativeStateObject(pipeline);
    }

    public static ID3D12RootSignature* GetNativeRootSignature(
        this ValidationLayer layer,
        Pipeline pipeline)
    {
        layer.RequireNativeResource(pipeline);
        return layer.RequireD3D12Backend().GetNativeRootSignature(pipeline);
    }

    public static ID3D12QueryHeap* GetNativeQueryHeap(
        this ValidationLayer layer,
        QueryPool pool)
    {
        layer.RequireNativeResource(pool);
        return layer.RequireD3D12Backend().GetNativeQueryHeap(pool);
    }

    public static ID3D12Fence* GetNativeTimeline(
        this ValidationLayer layer,
        ExternalTimeline timeline)
    {
        layer.RequireNativeResource(timeline);
        return layer.RequireD3D12Backend().GetNativeTimeline(timeline);
    }

    /// <summary>Imports a D3D12 Buffer pointer through the validated native-access boundary.</summary>
    public static Buffer ImportBuffer(
        this ValidationLayer layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        layer.ImportNativeBuffer(device, resource, ownership, desc, state);

    /// <summary>Imports a D3D12 Texture pointer through the validated native-access boundary.</summary>
    public static Texture ImportTexture(
        this ValidationLayer layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        layer.ImportNativeTexture(device, resource, ownership, desc, state);

    /// <summary>Imports a D3D12 Heap pointer through the validated native-access boundary.</summary>
    public static Heap ImportHeap(
        this ValidationLayer layer,
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc) =>
        layer.ImportNativeHeap(device, heap, ownership, desc);

    public static D3D12CommandListBorrow BorrowCommandList(
        this ValidationLayer layer,
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources) =>
        layer.BorrowNativeCommandList(context, retainedResources);

    public static D3D12CommandQueueLock LockCommandQueue(
        this ValidationLayer layer,
        Queue queue) =>
        layer.LockNativeCommandQueue(queue);
}

public sealed partial class ValidationLayer
{
    internal D3D12Backend RequireD3D12Backend() =>
        Backend as D3D12Backend ??
        throw new InvalidOperationException("The Validation Layer does not wrap D3D12Backend.");

    internal void RequireNativeDevice(Device device) => RequireDevice(device);

    internal void RequireNativeResource<T>(T resource)
        where T : DeviceResource =>
        Require(resource);

    internal void RequireNativePipeline(Pipeline pipeline, bool stateObject)
    {
        Require(pipeline);
        bool isStateObject = pipeline.Type is PipelineType.RayTracing or PipelineType.WorkGraph;
        if (isStateObject != stateObject)
        {
            Reject(
                "NativeAccess",
                stateObject
                    ? "GetNativeStateObject requires a RayTracing or WorkGraph Pipeline."
                    : "GetNativePipelineState requires a Graphics, Compute or Mesh Pipeline.",
                pipeline.Label);
        }
    }

    internal unsafe Buffer ImportNativeBuffer(
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        D3D12Backend backend = RequireD3D12NativeBackend(device);
        var validationState = new ResourceValidationState(buffer: true);
        ID3D12Resource* nativeResource = resource;
        BufferDesc createDesc = desc;
        ImportedResourceState importState = state;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Buffer? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = backend.ImportBuffer(
                    device,
                    nativeResource,
                    ownership,
                    createDesc,
                    importState);
                validationState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, validationState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    internal unsafe Texture ImportNativeTexture(
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        D3D12Backend backend = RequireD3D12NativeBackend(device);
        var validationState = new ResourceValidationState(buffer: false);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Texture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = backend.ImportTexture(device, resource, ownership, desc, state);
                validationState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, validationState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    internal unsafe Heap ImportNativeHeap(
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc)
    {
        D3D12Backend backend = RequireD3D12NativeBackend(device);
        var metadata = new HeapValidationState(desc.VisibleNodeMask);
        ID3D12Heap* nativeHeap = heap;
        HeapDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _heapStates.EnsureAdditionalCapacity();
            Heap? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = backend.ImportHeap(device, nativeHeap, ownership, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _heapStates.Add(result, metadata);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _heapStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    private D3D12Backend RequireD3D12NativeBackend(Device device)
    {
        RequireNativeDevice(device);
        RequireCapability<ExternalResources>(device);
        return Backend as D3D12Backend
            ?? throw new InvalidOperationException("The Validation Layer does not wrap D3D12Backend.");
    }

    internal D3D12CommandListBorrow BorrowNativeCommandList(
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources)
    {
        if (Backend is not D3D12Backend backend)
            throw new InvalidOperationException("The Validation Layer does not wrap D3D12Backend.");

        ContextValidationState state = RequireRecording(context);
        foreach (Resource resource in retainedResources)
            RequireOnDevice(context.Device, resource, "native command-list retained Resource");

        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (Resource resource in retainedResources)
                PrepareCommandDependencyCore(state, resource, ref capacity);
            ReserveCommandMutation(state, capacity);
        }

        D3D12CommandListBorrow result = backend.BorrowCommandList(context, retainedResources);
        lock (state)
        {
            foreach (Resource resource in retainedResources)
                RecordCommandDependencyCore(state, resource);
            state.Pipeline = null;
            state.PipelineType = null;
            state.WorkGraphBound = false;
            state.QueryPhases.Clear();
            state.ResourceStates.Clear();
        }
        return result;
    }

    internal D3D12CommandQueueLock LockNativeCommandQueue(Queue queue)
    {
        if (Backend is not D3D12Backend backend)
            throw new InvalidOperationException("The Validation Layer does not wrap D3D12Backend.");

        RequireQueue(queue);
        int threadId = Environment.CurrentManagedThreadId;
        NativeQueueLockValidationState state;
        lock (_gate)
        {
            if (!_nativeQueueLockStates.TryGetValue(queue, out state!))
            {
                _nativeQueueLockStates.EnsureCapacity(
                    checked(_nativeQueueLockStates.Count + 1));
                state = new NativeQueueLockValidationState();
                state.Lease = new ValidationQueueLockLease(this, queue, state);
                _nativeQueueLockStates.Add(queue, state);
            }
            if (state.OwnerThreadId == threadId)
            {
                Reject(
                    "Concurrency",
                    "The current thread already holds this Queue's native lock.");
            }
        }

        state.Lease!.WaitUntilReleased();
        D3D12CommandQueueLock inner = backend.LockCommandQueue(queue);
        D3D12CommandQueueLockLease innerLease = inner.Lease;
        ulong sequence = inner.Sequence;
        ValidationQueueLockLease lease = state.Lease!;
        lease.WaitUntilReleased();
        lease.Publish(innerLease, sequence, threadId);
        return new D3D12CommandQueueLock(lease, sequence);
    }

    private sealed unsafe class ValidationQueueLockLease : D3D12CommandQueueLockLease
    {
        private readonly ValidationLayer _owner;
        private readonly Queue _queue;
        private readonly NativeQueueLockValidationState _state;
        private D3D12CommandQueueLockLease? _inner;
        private ulong _innerSequence;

        internal ValidationQueueLockLease(
            ValidationLayer owner,
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
            int ownerThreadId)
        {
            _inner = inner;
            _innerSequence = sequence;
            try
            {
                lock (_owner._gate)
                    _state.OwnerThreadId = ownerThreadId;
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
                    state.OwnerThreadId != 0)
                {
                    state.OwnerThreadId = 0;
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
                    state.OwnerThreadId = 0;
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
