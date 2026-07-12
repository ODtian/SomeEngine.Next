using System.Numerics;

namespace SomeEngine.Graphics;

/// <summary>A single-use, single-thread command recording context.</summary>
public interface ICommandContext : IDisposable
{
    QueueType Queue { get; }
    bool IsFinished { get; }

    void Barriers(ReadOnlySpan<ResourceBarrier> barriers);
    void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong size);
    void CopyBufferToTexture(in BufferTextureCopy copy);
    void CopyTextureToBuffer(in TextureBufferCopy copy);
    void CopyTexture(in TextureToTextureCopy copy);
    void ResolveTexture(in TextureResolveRegion resolve);
    void ClearBuffer(BufferHandle buffer, in BufferRange range, uint pattern = 0);
    void ClearTexture(TextureHandle texture, in TextureSubresourceRange range, in Vector4 color);
    void ClearDepthStencilTexture(
        TextureHandle texture,
        in TextureSubresourceRange range,
        float depth = 1f,
        byte stencil = 0);

    void BeginRendering(in RenderingInfo rendering);
    void EndRendering();
    void SetPipeline(PipelineHandle pipeline);
    void SetBindGroup(uint groupIndex, BindGroupHandle group);
    void SetBindings(uint groupIndex, BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes);
    void SetPushConstants(
        PipelineLayoutHandle layout,
        ShaderStage stages,
        uint byteOffset,
        ReadOnlySpan<byte> data);
    void SetViewport(in Viewport viewport);
    void SetScissor(in Rect rect);
    void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, uint stride);
    void SetIndexBuffer(BufferHandle buffer, ulong offset, IndexFormat format);
    void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0);
    void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);
    void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    void DrawIndirect(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        BufferHandle countBuffer = default,
        ulong countBufferOffset = 0);
    void DrawIndexedIndirect(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        BufferHandle countBuffer = default,
        ulong countBufferOffset = 0);
    void DispatchIndirect(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        BufferHandle countBuffer = default,
        ulong countBufferOffset = 0);

    void ResetQueryPool(QueryPoolHandle pool, uint firstQuery, uint queryCount);
    void BeginQuery(QueryPoolHandle pool, uint queryIndex);
    void EndQuery(QueryPoolHandle pool, uint queryIndex);
    void WriteTimestamp(QueryPoolHandle pool, uint queryIndex);
    void ResolveQueryPool(
        QueryPoolHandle pool,
        uint firstQuery,
        uint queryCount,
        BufferHandle destination,
        ulong destinationOffset,
        ulong destinationStride = 0);

    void PushDebugGroup(string name);
    void PopDebugGroup();
    void InsertDebugMarker(string name);

    /// <summary>Closes the native command list and transfers its ownership back to the device.</summary>
    CommandListHandle Finish();
}

public enum ResourceState : ushort
{
    Common,
    CopySource,
    CopyDestination,
    ShaderResource,
    UnorderedAccess,
    RenderTarget,
    DepthWrite,
    DepthRead,
    VertexOrConstantBuffer,
    IndexBuffer,
    IndirectArgument,
    Present,
    ResolveSource,
    ResolveDestination,
}

public enum BarrierKind : byte
{
    Transition,
    UnorderedAccess,
    Aliasing,
}

public readonly record struct ResourceBarrier(
    BarrierKind Kind,
    ResourceHandle Resource,
    ResourceState Before,
    ResourceState After,
    TextureSubresourceRange TextureRange = default,
    ResourceHandle AliasingBefore = default)
{
    public static ResourceBarrier Transition(ResourceHandle resource, ResourceState before, ResourceState after, TextureSubresourceRange range = default) =>
        new(BarrierKind.Transition, resource, before, after, range);

    public static ResourceBarrier UnorderedAccess(ResourceHandle resource) =>
        new(BarrierKind.UnorderedAccess, resource, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess);

    public static ResourceBarrier Aliasing(ResourceHandle before, ResourceHandle after) =>
        new(BarrierKind.Aliasing, after, ResourceState.Common, ResourceState.Common, default, before);
}

/// <summary>A single-plane texture region. Array layers and three-dimensional depth are mutually exclusive.</summary>
public readonly record struct TextureCopyRegion(
    int MipLevel,
    int ArrayLayer,
    TextureAspect Aspect,
    int X,
    int Y,
    int Z,
    int Width,
    int Height,
    int Depth)
{
    public TextureCopyRegion(
        int mipLevel,
        int arrayLayer,
        TextureAspect aspect,
        int width,
        int height,
        int depth = 1)
        : this(mipLevel, arrayLayer, aspect, 0, 0, 0, width, height, depth) { }
}

/// <summary>Linear-buffer addressing for one texture-copy region.</summary>
public readonly record struct TextureBufferLayout(ulong Offset, uint BytesPerRow, uint RowsPerImage);

/// <summary>Backend-approved linear layout and minimum byte range for one texture-copy region.</summary>
public readonly record struct TextureCopyFootprint(
    TextureBufferLayout Layout,
    uint RowSizeInBytes,
    ulong FootprintSize)
{
    public ulong RequiredBufferSize => checked(Layout.Offset + FootprintSize);
}

public readonly record struct BufferTextureCopy(
    BufferHandle Source,
    TextureBufferLayout SourceLayout,
    TextureHandle Destination,
    TextureCopyRegion DestinationRegion);

public readonly record struct TextureBufferCopy(
    TextureHandle Source,
    TextureCopyRegion SourceRegion,
    BufferHandle Destination,
    TextureBufferLayout DestinationLayout);

/// <summary>
/// An exact texture-to-texture copy. Source and destination regions independently select their
/// mip, array layer, plane, origin, and extent; their extents and compatible formats must match.
/// </summary>
public readonly record struct TextureToTextureCopy(
    TextureHandle Source,
    TextureCopyRegion SourceRegion,
    TextureHandle Destination,
    TextureCopyRegion DestinationRegion);

/// <summary>The reduction used when resolving samples into one destination texel.</summary>
public enum ResolveMode : byte
{
    Average,
    Minimum,
    Maximum,
    SampleZero,
}

/// <summary>
/// Resolves one complete source subresource into one complete destination subresource.
/// Partial rectangles are intentionally outside the portable surface.
/// </summary>
public readonly record struct TextureResolveRegion(
    TextureHandle Source,
    TextureHandle Destination,
    int SourceMipLevel = 0,
    int SourceArrayLayer = 0,
    int DestinationMipLevel = 0,
    int DestinationArrayLayer = 0,
    TextureAspect Aspect = TextureAspect.Color,
    ResolveMode Mode = ResolveMode.Average);

public enum LoadAction : byte
{
    Load,
    Clear,
    Discard,
}

public enum StoreAction : byte
{
    Store,
    Discard,
}

public readonly record struct ColorAttachment(
    TextureViewHandle View,
    LoadAction Load,
    StoreAction Store,
    Vector4 ClearColor = default);

public readonly record struct DepthAttachmentOperations(
    LoadAction Load,
    StoreAction Store,
    bool ReadOnly = false,
    float ClearValue = 1f);

public readonly record struct StencilAttachmentOperations(
    LoadAction Load,
    StoreAction Store,
    bool ReadOnly = false,
    byte ClearValue = 0);

public readonly record struct DepthStencilAttachment(
    TextureViewHandle View,
    DepthAttachmentOperations? Depth,
    StencilAttachmentOperations? Stencil = null);

public readonly record struct RenderingInfo(
    ReadOnlyMemory<ColorAttachment> Colors,
    DepthStencilAttachment? DepthStencil,
    int Width,
    int Height);

public readonly record struct Viewport(float X, float Y, float Width, float Height, float MinDepth = 0f, float MaxDepth = 1f);
public readonly record struct Rect(int X, int Y, int Width, int Height);
public enum IndexFormat : byte { UInt16, UInt32 }
