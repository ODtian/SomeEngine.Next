namespace SomeEngine.Graphics;

public enum PrimitiveTopology : byte
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
}

public enum FillMode : byte
{
    Solid,
    Wireframe,
}

public enum CullMode : byte
{
    None,
    Front,
    Back,
}

public enum FrontFace : byte
{
    CounterClockwise,
    Clockwise,
}

public enum CompareOp : byte
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

public enum BlendFactor : byte
{
    Zero,
    One,
    SourceColor,
    OneMinusSourceColor,
    SourceAlpha,
    OneMinusSourceAlpha,
    DestinationColor,
    OneMinusDestinationColor,
    DestinationAlpha,
    OneMinusDestinationAlpha,
}

public enum BlendOperation : byte
{
    Add,
    Subtract,
    ReverseSubtract,
    Minimum,
    Maximum,
}

[Flags]
public enum ColorWriteMask : byte
{
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha,
}

public readonly record struct RasterizerDesc(
    FillMode Fill = FillMode.Solid,
    CullMode Cull = CullMode.Back,
    FrontFace FrontFace = FrontFace.CounterClockwise,
    bool DepthClip = true);

public readonly record struct DepthStencilDesc(
    bool DepthEnabled = false,
    bool DepthWrite = false,
    CompareOp DepthCompare = CompareOp.Less);

public readonly record struct BlendAttachmentDesc(
    bool Enabled = false,
    BlendFactor SourceColor = BlendFactor.One,
    BlendFactor DestinationColor = BlendFactor.Zero,
    BlendOperation ColorOperation = BlendOperation.Add,
    BlendFactor SourceAlpha = BlendFactor.One,
    BlendFactor DestinationAlpha = BlendFactor.Zero,
    BlendOperation AlphaOperation = BlendOperation.Add,
    ColorWriteMask WriteMask = ColorWriteMask.All);

public readonly record struct VertexAttributeDesc(uint Location, uint BufferSlot, Format Format, uint Offset);
public readonly record struct VertexBufferLayoutDesc(uint Slot, uint Stride, bool PerInstance = false, uint StepRate = 1);

public readonly record struct RasterPipelineDesc(
    PipelineLayoutHandle Layout,
    ShaderHandle VertexShader,
    ShaderHandle PixelShader,
    ReadOnlyMemory<Format> ColorFormats,
    Format DepthStencilFormat = Format.Unknown,
    PrimitiveTopology Topology = PrimitiveTopology.TriangleList,
    ReadOnlyMemory<VertexAttributeDesc> VertexAttributes = default,
    ReadOnlyMemory<VertexBufferLayoutDesc> VertexBuffers = default,
    RasterizerDesc Rasterizer = default,
    DepthStencilDesc DepthStencil = default,
    ReadOnlyMemory<BlendAttachmentDesc> BlendAttachments = default,
    int SampleCount = 1,
    string? Name = null,
    PipelineCacheKey CacheKey = default);

public readonly record struct ComputePipelineDesc(
    PipelineLayoutHandle Layout,
    ShaderHandle Shader,
    string? Name = null,
    PipelineCacheKey CacheKey = default);
