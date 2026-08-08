using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Validation;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Static validation surface for borrowed D3D12 pointers and explicit
/// Borrowed/Transferred native imports. Getters never AddRef. Command-list retention input is consumed
/// synchronously and follows the Device RetirementType.</para>
/// <para><b>After Dispose:</b> This type has no Dispose state; disposing the layer or owning object
/// invalidates every pointer and borrow obtained through it.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public static unsafe class D3D12ValidationNativeAccess
{
    public static ID3D12Device10* GetNativeDevice(
        this ValidationLayer<D3D12Backend> layer,
        Device device)
    {
        layer.RequireNativeDevice(device);
        return layer.NativeBackend.GetNativeDevice(device);
    }

    public static IDXGIAdapter4* GetNativeAdapter(
        this ValidationLayer<D3D12Backend> layer,
        Device device)
    {
        layer.RequireNativeDevice(device);
        return layer.NativeBackend.GetNativeAdapter(device);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer<D3D12Backend> layer,
        Buffer buffer)
    {
        layer.RequireNativeResource(buffer);
        return layer.NativeBackend.GetNativeResource(buffer);
    }

    public static ID3D12Resource* GetNativeResource(
        this ValidationLayer<D3D12Backend> layer,
        Texture texture)
    {
        layer.RequireNativeResource(texture);
        return layer.NativeBackend.GetNativeResource(texture);
    }

    public static ID3D12Heap* GetNativeHeap(
        this ValidationLayer<D3D12Backend> layer,
        Heap heap)
    {
        layer.RequireNativeResource(heap);
        return layer.NativeBackend.GetNativeHeap(heap);
    }

    public static ID3D12PipelineState* GetNativePipelineState(
        this ValidationLayer<D3D12Backend> layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: false);
        return layer.NativeBackend.GetNativePipelineState(pipeline);
    }

    public static ID3D12StateObject* GetNativeStateObject(
        this ValidationLayer<D3D12Backend> layer,
        Pipeline pipeline)
    {
        layer.RequireNativePipeline(pipeline, stateObject: true);
        return layer.NativeBackend.GetNativeStateObject(pipeline);
    }

    public static ID3D12RootSignature* GetNativeRootSignature(
        this ValidationLayer<D3D12Backend> layer,
        Pipeline pipeline)
    {
        layer.RequireNativeResource(pipeline);
        return layer.NativeBackend.GetNativeRootSignature(pipeline);
    }

    public static ID3D12QueryHeap* GetNativeQueryHeap(
        this ValidationLayer<D3D12Backend> layer,
        QueryPool pool)
    {
        layer.RequireNativeResource(pool);
        return layer.NativeBackend.GetNativeQueryHeap(pool);
    }

    public static ID3D12Fence* GetNativeTimeline(
        this ValidationLayer<D3D12Backend> layer,
        ExternalTimeline timeline)
    {
        layer.RequireNativeResource(timeline);
        return layer.NativeBackend.GetNativeTimeline(timeline);
    }

    /// <summary>Imports a D3D12 Buffer pointer through the validated native-access boundary.</summary>
    public static Buffer ImportBuffer(
        this ValidationLayer<D3D12Backend> layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        layer.ImportNativeBuffer(device, resource, ownership, desc, state);

    /// <summary>Imports a D3D12 Texture pointer through the validated native-access boundary.</summary>
    public static Texture ImportTexture(
        this ValidationLayer<D3D12Backend> layer,
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        layer.ImportNativeTexture(device, resource, ownership, desc, state);

    /// <summary>Imports a D3D12 Heap pointer through the validated native-access boundary.</summary>
    public static Heap ImportHeap(
        this ValidationLayer<D3D12Backend> layer,
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc) =>
        layer.ImportNativeHeap(device, heap, ownership, desc);

    public static D3D12CommandListBorrow BorrowCommandList(
        this ValidationLayer<D3D12Backend> layer,
        CommandContext context,
        ReadOnlySpan<Resource> retainedResources) =>
        layer.BorrowNativeCommandList(context, retainedResources);

    public static D3D12CommandQueueLock LockCommandQueue(
        this ValidationLayer<D3D12Backend> layer,
        Queue queue) =>
        layer.LockNativeCommandQueue(queue);
}

public sealed partial class ValidationLayer<TBackend>
{
    internal TBackend NativeBackend => Backend;

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
        Buffer result = backend.ImportBuffer(device, resource, ownership, desc, state);
        try
        {
            return Track(result, device);
        }
        catch
        {
            result.Dispose();
            throw;
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
        Texture result = backend.ImportTexture(device, resource, ownership, desc, state);
        try
        {
            return Track(result, device);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal unsafe Heap ImportNativeHeap(
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc)
    {
        D3D12Backend backend = RequireD3D12NativeBackend(device);
        Heap result = backend.ImportHeap(device, heap, ownership, desc);
        try
        {
            return Track(result, device);
        }
        catch
        {
            result.Dispose();
            throw;
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

        D3D12CommandListBorrow result = backend.BorrowCommandList(context, retainedResources);
        lock (state)
        {
            foreach (Resource resource in retainedResources)
                RecordCommandDependencyCore(state, resource);
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
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
        if (backend.IsCommandQueueLockHeldByCurrentThread(queue))
        {
            Reject(
                "Concurrency",
                "The current thread already holds this Queue's native lock.");
        }
        return backend.LockCommandQueue(queue);
    }
}
