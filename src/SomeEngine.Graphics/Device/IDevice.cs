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

    /// <summary>An immutable, thread-safe snapshot used by render-graph compilation.</summary>
    DeviceCompilationSnapshot Compilation { get; }

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
    void DestroyShader(ShaderHandle shader);
    void DestroyPipelineLayout(PipelineLayoutHandle layout);
    void DestroyPipeline(PipelineHandle pipeline);

    void WriteBuffer(BufferHandle buffer, ulong offset, ReadOnlySpan<byte> data);
    void ReadBuffer(BufferHandle buffer, ulong offset, Span<byte> destination);

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

    /// <summary>Reclaims objects whose exact queue completions have passed.</summary>
    int CollectGarbage();

    GraphicsDiagnostic[] DrainDiagnostics();
}
