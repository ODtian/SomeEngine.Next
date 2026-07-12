namespace SomeEngine.Graphics;

/// <summary>
/// Owns one graphics execution domain. Resource creation and submission belong to the
/// coordinating thread; command contexts may be recorded by independent worker threads.
/// </summary>
public interface IDevice : IDisposable
{
    /// <summary>The opaque identity carried by every handle and completion from this device.</summary>
    DeviceDomain Domain { get; }

    DeviceInfo Info { get; }

    /// <summary>Immutable fail-closed device, adapter, feature, and limit facts.</summary>
    DeviceCapabilities Capabilities { get; }

    /// <summary>An immutable, thread-safe snapshot used by render-graph compilation.</summary>
    DeviceCompilationSnapshot Compilation { get; }

    /// <summary>The most recent durable device error. Draining diagnostics does not clear it.</summary>
    DeviceError LastError { get; }

    FormatSupport GetFormatSupport(Format format);
    MemoryBudget GetMemoryBudget(MemoryType memoryType);
    ResourceMemoryInfo GetResourceMemoryInfo(ResourceHandle resource);
    void SetResidencyPriority(ResourceHandle resource, ResidencyPriority priority);

    ResourceRequirements GetBufferRequirements(
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal);
    ResourceRequirements GetTextureRequirements(in TextureDesc desc);
    TextureCopyFootprint GetTextureCopyFootprint(
        in TextureDesc desc,
        in TextureCopyRegion region,
        ulong requestedBufferOffset = 0);

    /// <summary>Returns immutable metadata for an exact live buffer handle.</summary>
    BufferMetadata GetBufferMetadata(BufferHandle buffer);

    /// <summary>Returns immutable metadata for an exact live texture handle.</summary>
    TextureMetadata GetTextureMetadata(TextureHandle texture);

    HeapHandle CreateHeap(in HeapDesc desc);
    BufferHandle CreateBuffer(in BufferDesc desc, MemoryType memoryType = MemoryType.DeviceLocal);
    TextureHandle CreateTexture(in TextureDesc desc);
    BufferHandle CreatePlacedBuffer(HeapHandle heap, ulong offset, in BufferDesc desc);
    TextureHandle CreatePlacedTexture(HeapHandle heap, ulong offset, in TextureDesc desc);
    void DestroyHeap(HeapHandle heap);
    void DestroyBuffer(BufferHandle buffer);
    void DestroyTexture(TextureHandle texture);

    TextureViewHandle CreateTextureView(in TextureViewDesc desc);
    BufferViewHandle CreateBufferView(in BufferViewDesc desc);
    SamplerHandle CreateSampler(in SamplerDesc desc);
    void DestroyTextureView(TextureViewHandle view);
    void DestroyBufferView(BufferViewHandle view);
    void DestroySampler(SamplerHandle sampler);

    BindGroupLayoutHandle CreateBindGroupLayout(ReadOnlySpan<BindingDesc> bindings);
    BindGroupHandle CreateBindGroup(BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes, string? name = null);
    void DestroyBindGroupLayout(BindGroupLayoutHandle layout);
    void DestroyBindGroup(BindGroupHandle group);

    ShaderHandle CreateShader(in ShaderDesc desc);
    PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDesc desc);
    PipelineHandle CreateRasterPipeline(in RasterPipelineDesc desc);
    PipelineHandle CreateComputePipeline(in ComputePipelineDesc desc);
    PipelineMetadata GetPipelineMetadata(PipelineHandle pipeline);
    PipelineStatus GetPipelineStatus(PipelineHandle pipeline);
    PipelineCacheStats GetPipelineCacheStats();
    void InvalidatePipelineCache(PipelineCacheKey key);
    void InvalidateAllPipelines();
    void DestroyShader(ShaderHandle shader);
    void DestroyPipelineLayout(PipelineLayoutHandle layout);
    void DestroyPipeline(PipelineHandle pipeline);

    QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc);
    QueryPoolMetadata GetQueryPoolMetadata(QueryPoolHandle pool);
    void DestroyQueryPool(QueryPoolHandle pool);
    ulong GetTimestampFrequency(QueueType queue);
    TimestampCalibration GetTimestampCalibration(QueueType queue);

    SwapchainHandle CreateSwapchain(in SwapchainDesc desc);
    SwapchainImage AcquireNextImage(SwapchainHandle swapchain);
    PresentResult Present(SwapchainHandle swapchain, uint imageIndex, in PresentOptions options = default);
    void Resize(SwapchainHandle swapchain, int width, int height);
    void DestroySwapchain(SwapchainHandle swapchain);

    BindlessTableHandle CreateBindlessTable(in BindlessTableDesc desc);
    void DestroyBindlessTable(BindlessTableHandle table);
    BindlessSlot AllocateBindlessSlot(BindlessTableHandle table);
    void FreeBindlessSlot(in BindlessSlot slot);
    void WriteBindlessTexture(in BindlessSlot slot, TextureViewHandle view);
    void WriteBindlessBuffer(in BindlessSlot slot, BufferViewHandle view);
    void WriteBindlessSampler(in BindlessSlot slot, SamplerHandle sampler);

    void WriteBuffer(BufferHandle buffer, ulong offset, ReadOnlySpan<byte> data);
    void ReadBuffer(BufferHandle buffer, ulong offset, Span<byte> destination);
    BufferMapping MapBuffer(BufferHandle buffer, BufferMapMode mode, in BufferRange range);

    void SetName(HeapHandle heap, string? name);
    void SetName(BufferHandle buffer, string? name);
    void SetName(TextureHandle texture, string? name);
    void SetName(TextureViewHandle view, string? name);
    void SetName(BufferViewHandle view, string? name);
    void SetName(SamplerHandle sampler, string? name);
    void SetName(BindGroupLayoutHandle layout, string? name);
    void SetName(BindGroupHandle group, string? name);
    void SetName(ShaderHandle shader, string? name);
    void SetName(PipelineLayoutHandle layout, string? name);
    void SetName(PipelineHandle pipeline, string? name);
    void SetName(CommandListHandle commandList, string? name);
    void SetName(QueryPoolHandle pool, string? name);
    void SetName(SwapchainHandle swapchain, string? name);
    void SetName(BindlessTableHandle table, string? name);

    /// <summary>
    /// Acquires an exclusive allocator/list pair. The returned context is single-thread owned
    /// from its first recording operation until <see cref="ICommandContext.Finish"/>.
    /// </summary>
    ICommandContext AcquireCommandContext(QueueType queue, string? name = null);

    /// <summary>Consumes each command list exactly once and returns its queue completion.</summary>
    GpuCompletion Submit(QueueType queue, ReadOnlySpan<CommandListHandle> commandLists, ReadOnlySpan<GpuCompletion> waits = default);

    /// <summary>Releases a finished command list that will not be submitted.</summary>
    void DiscardCommandList(CommandListHandle commandList);

    /// <summary>Returns the completed value for one queue. This query is thread-safe.</summary>
    ulong GetCompletedValue(QueueType queue);

    /// <summary>
    /// Waits for an already-published completion from this device. This operation is thread-safe;
    /// invalid, cross-device, and future/unpublished completions are rejected before blocking.
    /// </summary>
    bool Wait(in GpuCompletion completion, TimeSpan timeout);

    /// <summary>
    /// Snapshots every queue's already-published completion and waits for precisely that work.
    /// Work submitted after the snapshot is intentionally not part of this wait.
    /// </summary>
    bool WaitIdle(TimeSpan timeout);

    /// <summary>Reclaims objects whose exact queue completions have passed.</summary>
    int CollectGarbage();

    GraphicsDiagnostic[] DrainDiagnostics();
}
