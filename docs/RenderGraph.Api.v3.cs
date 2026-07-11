// SUPERSEDED RESEARCH INPUT — not an accepted public contract.
// See wiki/architecture/Render-Graph.md and docs/adr/0005-ue-style-immediate-render-graph.md.
// This draft mixes graph-owned history/persistence/bindless policy, public store operations,
// backend-state-like access flags, and other semantics rejected by the accepted architecture.
#nullable enable

using System;
using System.Numerics;

namespace Engine.Rendering;

public sealed class RenderGraph : IDisposable
{
    public TextureHandle CreateTexture(in TextureDesc desc) => default;
    public BufferHandle CreateBuffer(in BufferDesc desc) => default;

    public TextureHandle ImportTexture(Texture texture, in ImportResourceParams importParams) => default;
    public BufferHandle ImportBuffer(BufferHandle buffer, in ImportResourceParams importParams) => default;
    public TextureHandle ImportBackbuffer(Texture texture, in ImportResourceParams importParams) => default;
    public RayTracingAccelerationStructureHandle ImportRayTracingAccelerationStructure(
        RayTracingAccelerationStructure accelerationStructure,
        string name) => default;

    public RenderGraphBuilder AddPass<TData>(
        string name,
        QueueType queue,
        out TData data)
        where TData : class, new()
    {
        data = new TData();
        return default;
    }

    public void BeginRecording(in RenderGraphParameters parameters) { }
    public void EndRecordingAndExecute() { }
    public void EndFrame() { }
    public void Dispose() { }
}

public struct RenderGraphBuilder : IDisposable
{
    public TextureHandle ReadTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead) => input;

    public TextureHandle WriteTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderWrite,
        AccessFlags flags = AccessFlags.Write) => input;

    public TextureHandle ReadWriteTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead | ResourceAccess.ShaderWrite) => input;

    public BufferHandle ReadBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead) => input;

    public BufferHandle WriteBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderWrite,
        AccessFlags flags = AccessFlags.Write) => input;

    public BufferHandle ReadWriteBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead | ResourceAccess.ShaderWrite) => input;

    public RayTracingAccelerationStructureHandle ReadRayTracingAccelerationStructure(
        in RayTracingAccelerationStructureHandle input) => input;

    public RayTracingAccelerationStructureHandle WriteRayTracingAccelerationStructure(
        in RayTracingAccelerationStructureHandle input) => input;

    public TextureHandle SetRenderAttachment(
        TextureHandle texture,
        int index,
        in RenderAttachmentDesc attachment,
        AccessFlags flags = AccessFlags.Write) => texture;

    public TextureHandle SetRenderAttachmentDepth(
        TextureHandle texture,
        in RenderAttachmentDesc attachment,
        AccessFlags flags = AccessFlags.ReadWrite) => texture;

    public void SetInputAttachment(
        TextureHandle texture,
        int index,
        in TextureViewDesc view) { }

    public TextureHandle SetRandomAccessAttachment(
        TextureHandle texture,
        int index,
        in TextureViewDesc view,
        AccessFlags flags = AccessFlags.ReadWrite) => texture;

    public BufferHandle SetRandomAccessAttachment(
        BufferHandle buffer,
        int index,
        in BufferViewDesc view,
        AccessFlags flags = AccessFlags.ReadWrite) => buffer;

    public void SetShadingRateImageAttachment(in TextureHandle texture) { }
    public void SetShadingRateFragmentSize(ShadingRateFragmentSize fragmentSize) { }
    public void SetShadingRateCombiner(
        ShadingRateCombinerStage stage,
        ShadingRateCombiner combiner) { }
    public void SetViewCount(int viewCount) { }

    public void EnableAsyncCompute(bool value) { }
    public void AllowPassCulling(bool value) { }
    public void AllowGlobalStateModification(bool value) { }
    public void EnableFoveatedRasterization(bool value) { }
    public void GenerateDebugData(bool value) { }

    public void SetRenderFunc<TData>(
        Action<TData, RenderGraphContext> renderFunc)
        where TData : class, new() { }

    public void Dispose() { }
}

public readonly struct TextureHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct BufferHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct RayTracingAccelerationStructureHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct TextureSubresourceRange
{
    public static TextureSubresourceRange All => default;

    public int MipIndex { get; init; }
    public int NumMips { get; init; }
    public int ArraySlice { get; init; }
    public int NumArraySlices { get; init; }
    public int PlaneSlice { get; init; }
    public int NumPlaneSlices { get; init; }
    public TextureAspect Aspect { get; init; }
}

public readonly struct BufferRange
{
    public static BufferRange All => new() { Offset = 0, Size = -1 };

    public long Offset { get; init; }
    public long Size { get; init; }
}

public readonly struct TextureViewDesc
{
    public TextureSubresourceRange Range { get; init; }
    public Format Format { get; init; }
    public TextureDimension Dimension { get; init; }
    public int HistoryIndex { get; init; }
}

public readonly struct BufferViewDesc
{
    public BufferRange Range { get; init; }
    public Format Format { get; init; }
    public int Stride { get; init; }
    public int HistoryIndex { get; init; }
}

public readonly struct RenderAttachmentDesc
{
    public TextureViewDesc View { get; init; }
    public RenderBufferLoadAction LoadAction { get; init; }
    public RenderBufferStoreAction StoreAction { get; init; }
    public ClearValue ClearValue { get; init; }
    public TextureHandle ResolveTexture { get; init; }
    public TextureViewDesc ResolveView { get; init; }
    public ResolveMode ResolveMode { get; init; }
}

public readonly struct TextureDesc
{
    public string Name { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public int ArraySize { get; init; }
    public int MipCount { get; init; }
    public int SampleCount { get; init; }
    public int Alignment { get; init; }
    public int HistoryCount { get; init; }
    public Vector2 Scale { get; init; }
    public TextureHandle RelativeTo { get; init; }
    public Format Format { get; init; }
    public TextureDimension Dimension { get; init; }
    public TextureSizeMode SizeMode { get; init; }
    public TextureFlags Flags { get; init; }
    public ResizeMode ResizeMode { get; init; }
    public ClearValue ClearValue { get; init; }
}

public readonly struct BufferDesc
{
    public string Name { get; init; }
    public long Size { get; init; }
    public int Stride { get; init; }
    public int Alignment { get; init; }
    public int HistoryCount { get; init; }
    public BufferFlags Flags { get; init; }
    public ResizeMode ResizeMode { get; init; }
}

public readonly struct ImportResourceParams
{
    public ResourceAccess InitialAccess { get; init; }
    public ResourceAccess FinalAccess { get; init; }
    public QueueType Queue { get; init; }
    public ulong WaitValue { get; init; }
    public ulong SignalValue { get; init; }
    public bool PreserveContents { get; init; }
}

public readonly struct RenderGraphParameters
{
    public string ExecutionName { get; init; }
    public ulong FrameIndex { get; init; }
    public ulong GpuCompletedFrameIndex { get; init; }
    public bool ResetHistory { get; init; }
    public int MaxFramesInFlight { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public bool GenerateDebugData { get; init; }
}

public readonly struct ClearValue
{
    public Vector4 Color { get; init; }
    public float Depth { get; init; }
    public uint Stencil { get; init; }
}

[Flags]
public enum AccessFlags
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
    Discard = 4,
    WriteAll = Write | Discard,
}

[Flags]
public enum ResourceAccess : ulong
{
    None = 0,
    ShaderRead = 1UL << 0,
    ShaderWrite = 1UL << 1,
    RenderTarget = 1UL << 2,
    DepthRead = 1UL << 3,
    DepthWrite = 1UL << 4,
    CopySource = 1UL << 5,
    CopyDestination = 1UL << 6,
    ResolveSource = 1UL << 7,
    ResolveDestination = 1UL << 8,
    VertexBuffer = 1UL << 9,
    IndexBuffer = 1UL << 10,
    ConstantBuffer = 1UL << 11,
    IndirectArguments = 1UL << 12,
    Predication = 1UL << 13,
    AccelerationStructureRead = 1UL << 14,
    AccelerationStructureWrite = 1UL << 15,
    ShadingRate = 1UL << 16,
    InputAttachment = 1UL << 17,
    Present = 1UL << 18,
    HostRead = 1UL << 19,
    HostWrite = 1UL << 20,
    SparseBinding = 1UL << 21,
}

[Flags]
public enum TextureFlags
{
    None = 0,
    Persistent = 1 << 0,
    Memoryless = 1 << 1,
    Sparse = 1 << 2,
    Bindless = 1 << 3,
    Exportable = 1 << 4,
    Aliasable = 1 << 5,
}

[Flags]
public enum BufferFlags
{
    None = 0,
    Persistent = 1 << 0,
    Sparse = 1 << 1,
    Bindless = 1 << 2,
    Exportable = 1 << 3,
    Aliasable = 1 << 4,
    AccelerationStructure = 1 << 5,
    IndirectArguments = 1 << 6,
    Predication = 1 << 7,
    Counter = 1 << 8,
}

[Flags]
public enum TextureAspect
{
    None = 0,
    Color = 1 << 0,
    Depth = 1 << 1,
    Stencil = 1 << 2,
    Plane0 = 1 << 3,
    Plane1 = 1 << 4,
    Plane2 = 1 << 5,
}

public enum QueueType
{
    Graphics,
    Compute,
    Copy,
}

public enum TextureSizeMode
{
    Explicit,
    Scale,
    Relative,
}

public enum ResizeMode
{
    Discard,
    Copy,
    Resample,
    Clear,
}

public enum RenderBufferLoadAction
{
    Load,
    Clear,
    DontCare,
}

public enum RenderBufferStoreAction
{
    Store,
    DontCare,
    Resolve,
    StoreAndResolve,
}

public enum ResolveMode
{
    None,
    Average,
    Min,
    Max,
    SampleZero,
}

public enum ShadingRateFragmentSize
{
    FragmentSize1x1,
    FragmentSize1x2,
    FragmentSize2x1,
    FragmentSize2x2,
    FragmentSize2x4,
    FragmentSize4x2,
    FragmentSize4x4,
}

public enum ShadingRateCombinerStage
{
    Primitive,
    Fragment,
}

public enum ShadingRateCombiner
{
    Keep,
    Replace,
    Min,
    Max,
    Multiply,
}

public enum Format
{
    Unknown,
    R8G8B8A8UNorm,
    R16G16B16A16Float,
    D32Float,
    D24UNormS8UInt,
}

public enum TextureDimension
{
    Texture1D,
    Texture2D,
    Texture3D,
    Cube,
    Texture2DArray,
    CubeArray,
}

public sealed class RenderGraphContext
{
    public CommandBuffer CommandBuffer => throw new NotSupportedException();
    public Texture GetTexture(TextureHandle handle) => throw new NotSupportedException();
    public TextureView GetTextureView(TextureHandle handle, in TextureViewDesc view) => throw new NotSupportedException();
    public BufferHandle GetBuffer(BufferHandle handle) => throw new NotSupportedException();
    public BufferView GetBufferView(BufferHandle handle, in BufferViewDesc view) => throw new NotSupportedException();
    public RayTracingAccelerationStructure GetRayTracingAccelerationStructure(
        RayTracingAccelerationStructureHandle handle) => throw new NotSupportedException();
}

public abstract class Texture { }
public abstract class TextureView { }
public abstract class BufferHandle { }
public abstract class BufferView { }
public abstract class CommandBuffer { }
public abstract class RayTracingAccelerationStructure { }
