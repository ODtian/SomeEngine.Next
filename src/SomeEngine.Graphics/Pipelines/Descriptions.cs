using System.Numerics;
using SlangShaderSharp;

namespace SomeEngine.Graphics;

public enum PrimitiveTopology : byte
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
    PatchList,
}

public enum StripCut : byte
{
    Disabled,
    UInt16,
    UInt32,
}

public enum FillType : byte
{
    Solid,
    Wireframe,
}

public enum CullType : byte
{
    None,
    Front,
    Back,
}

public enum FrontFace : byte
{
    Clockwise,
    CounterClockwise,
}

public enum StencilOperation : byte
{
    Keep,
    Zero,
    Replace,
    IncrementClamp,
    DecrementClamp,
    Invert,
    IncrementWrap,
    DecrementWrap,
}

public enum BlendFactor : byte
{
    Zero,
    One,
    SourceColor,
    OneMinusSourceColor,
    SourceAlpha,
    OneMinusSourceAlpha,
    DestinationAlpha,
    OneMinusDestinationAlpha,
    DestinationColor,
    OneMinusDestinationColor,
    SourceAlphaSaturate,
    BlendConstant,
    OneMinusBlendConstant,
    Source1Color,
    OneMinusSource1Color,
    Source1Alpha,
    OneMinusSource1Alpha,
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
public enum ColorWriteMasks : byte
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha,
}

[Flags]
public enum DynamicStates : ushort
{
    None = 0,
    Viewport = 1 << 0,
    Scissor = 1 << 1,
    BlendConstants = 1 << 2,
    StencilReference = 1 << 3,
    DepthBounds = 1 << 4,
    DepthBias = 1 << 5,
    PrimitiveTopology = 1 << 6,
    StripCut = 1 << 7,
}

public readonly record struct RasterizerState(
    FillType Fill = FillType.Solid,
    CullType Cull = CullType.Back,
    FrontFace FrontFace = FrontFace.CounterClockwise,
    int DepthBias = 0,
    float DepthBiasClamp = 0,
    float SlopeScaledDepthBias = 0,
    bool DepthClip = true,
    bool ConservativeRasterization = false);

public readonly record struct MultisampleState(
    uint SampleCount = 1,
    uint SampleMask = uint.MaxValue,
    bool AlphaToCoverage = false);

public readonly record struct StencilFaceState(
    StencilOperation Fail,
    StencilOperation DepthFail,
    StencilOperation Pass,
    CompareOperation Comparison);

public readonly record struct DepthStencilState(
    bool DepthTest = false,
    bool DepthWrite = false,
    CompareOperation DepthComparison = CompareOperation.Less,
    bool DepthBoundsTest = false,
    bool StencilTest = false,
    byte StencilReadMask = byte.MaxValue,
    byte StencilWriteMask = byte.MaxValue,
    StencilFaceState Front = default,
    StencilFaceState Back = default);

public readonly record struct BlendAttachmentState(
    bool Enabled = false,
    BlendFactor SourceColor = BlendFactor.One,
    BlendFactor DestinationColor = BlendFactor.Zero,
    BlendOperation ColorOperation = BlendOperation.Add,
    BlendFactor SourceAlpha = BlendFactor.One,
    BlendFactor DestinationAlpha = BlendFactor.Zero,
    BlendOperation AlphaOperation = BlendOperation.Add,
    ColorWriteMasks WriteMask = ColorWriteMasks.All);

public readonly ref struct BlendState
{
    public BlendState(
        ReadOnlySpan<BlendAttachmentState> attachments,
        bool independentBlend = false,
        bool logicOperationEnabled = false)
    {
        Attachments = attachments;
        IndependentBlend = independentBlend;
        LogicOperationEnabled = logicOperationEnabled;
    }

    public ReadOnlySpan<BlendAttachmentState> Attachments { get; }
    public bool IndependentBlend { get; }
    public bool LogicOperationEnabled { get; }
}

public readonly record struct VertexAttribute(
    uint Location,
    uint BufferIndex,
    Format Format,
    uint Offset);

public readonly record struct VertexBufferLayout(
    uint BufferIndex,
    uint Stride,
    bool PerInstance = false,
    uint InstanceStepRate = 1);

public readonly ref struct AttachmentFormatSignature
{
    public AttachmentFormatSignature(
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat,
        uint sampleCount = 1)
    {
        ColorFormats = colorFormats;
        DepthStencilFormat = depthStencilFormat;
        SampleCount = sampleCount;
    }

    public ReadOnlySpan<Format> ColorFormats { get; }
    public Format? DepthStencilFormat { get; }
    public uint SampleCount { get; }
}

public readonly struct StreamOutputElement
{
    private StreamOutputElement(
        VariableLayoutReflection variable,
        bool gap,
        uint stream,
        byte startComponent,
        byte componentCount,
        byte outputSlot)
    {
        Variable = variable;
        IsGap = gap;
        Stream = stream;
        StartComponent = startComponent;
        ComponentCount = componentCount;
        OutputSlot = outputSlot;
    }

    public VariableLayoutReflection Variable { get; }
    public bool IsGap { get; }
    public uint Stream { get; }
    public byte StartComponent { get; }
    public byte ComponentCount { get; }
    public byte OutputSlot { get; }

    public static StreamOutputElement Output(
        VariableLayoutReflection variable,
        uint stream,
        byte startComponent,
        byte componentCount,
        byte outputSlot) =>
        new(variable, false, stream, startComponent, componentCount, outputSlot);

    public static StreamOutputElement Gap(
        uint stream,
        byte componentCount,
        byte outputSlot) =>
        new(VariableLayoutReflection.Null, true, stream, 0, componentCount, outputSlot);
}

public readonly ref struct StreamOutputState
{
    public StreamOutputState(
        ReadOnlySpan<StreamOutputElement> elements,
        ReadOnlySpan<uint> bufferStrides,
        uint? rasterizedStreamIndex)
    {
        Elements = elements;
        BufferStrides = bufferStrides;
        RasterizedStreamIndex = rasterizedStreamIndex;
    }

    public ReadOnlySpan<StreamOutputElement> Elements { get; }
    public ReadOnlySpan<uint> BufferStrides { get; }
    public uint? RasterizedStreamIndex { get; }
}

public readonly ref struct GraphicsPipelineDesc
{
    public GraphicsPipelineDesc(
        IComponentType program,
        EntryPointReflection vertex,
        EntryPointReflection pixel,
        ReadOnlySpan<VertexBufferLayout> vertexBuffers,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        PrimitiveTopology topology,
        StripCut stripCut,
        in RasterizerState rasterizer,
        in MultisampleState multisample,
        in DepthStencilState depthStencil,
        in BlendState blend,
        in AttachmentFormatSignature attachments,
        DynamicStates dynamicStates = DynamicStates.None,
        string? label = null)
    {
        Program = program;
        Vertex = vertex;
        Pixel = pixel;
        VertexBuffers = vertexBuffers;
        VertexAttributes = vertexAttributes;
        Topology = topology;
        StripCut = stripCut;
        Rasterizer = rasterizer;
        Multisample = multisample;
        DepthStencil = depthStencil;
        Blend = blend;
        Attachments = attachments;
        DynamicStates = dynamicStates;
        StreamOutput = default;
        HasStreamOutput = false;
        Label = label;
    }

    public GraphicsPipelineDesc(
        IComponentType program,
        EntryPointReflection vertex,
        EntryPointReflection pixel,
        ReadOnlySpan<VertexBufferLayout> vertexBuffers,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        PrimitiveTopology topology,
        StripCut stripCut,
        in RasterizerState rasterizer,
        in MultisampleState multisample,
        in DepthStencilState depthStencil,
        in BlendState blend,
        in AttachmentFormatSignature attachments,
        in StreamOutputState streamOutput,
        DynamicStates dynamicStates = DynamicStates.None,
        string? label = null)
        : this(
            program,
            vertex,
            pixel,
            vertexBuffers,
            vertexAttributes,
            topology,
            stripCut,
            rasterizer,
            multisample,
            depthStencil,
            blend,
            attachments,
            dynamicStates,
            label)
    {
        StreamOutput = streamOutput;
        HasStreamOutput = true;
    }

    public IComponentType Program { get; }
    public EntryPointReflection Vertex { get; }
    public EntryPointReflection Pixel { get; }
    public ReadOnlySpan<VertexBufferLayout> VertexBuffers { get; }
    public ReadOnlySpan<VertexAttribute> VertexAttributes { get; }
    public PrimitiveTopology Topology { get; }
    public StripCut StripCut { get; }
    public RasterizerState Rasterizer { get; }
    public MultisampleState Multisample { get; }
    public DepthStencilState DepthStencil { get; }
    public BlendState Blend { get; }
    public AttachmentFormatSignature Attachments { get; }
    public DynamicStates DynamicStates { get; }
    public StreamOutputState StreamOutput { get; }
    public bool HasStreamOutput { get; }
    public string? Label { get; }
}

public readonly record struct ComputePipelineDesc(
    IComponentType Program,
    EntryPointReflection Compute,
    string? Label = null);

public readonly ref struct MeshPipelineDesc
{
    public MeshPipelineDesc(
        IComponentType program,
        EntryPointReflection mesh,
        EntryPointReflection amplification,
        EntryPointReflection pixel,
        in RasterizerState rasterizer,
        in MultisampleState multisample,
        in DepthStencilState depthStencil,
        in BlendState blend,
        in AttachmentFormatSignature attachments,
        DynamicStates dynamicStates = DynamicStates.None,
        string? label = null)
    {
        Program = program;
        Mesh = mesh;
        Amplification = amplification;
        Pixel = pixel;
        Rasterizer = rasterizer;
        Multisample = multisample;
        DepthStencil = depthStencil;
        Blend = blend;
        Attachments = attachments;
        DynamicStates = dynamicStates;
        Label = label;
    }

    public IComponentType Program { get; }
    public EntryPointReflection Mesh { get; }
    public EntryPointReflection Amplification { get; }
    public EntryPointReflection Pixel { get; }
    public RasterizerState Rasterizer { get; }
    public MultisampleState Multisample { get; }
    public DepthStencilState DepthStencil { get; }
    public BlendState Blend { get; }
    public AttachmentFormatSignature Attachments { get; }
    public DynamicStates DynamicStates { get; }
    public string? Label { get; }
}
