namespace SomeEngine.Graphics;

[Flags]
public enum IndirectArgumentTypes : ushort
{
    None = 0,
    Draw = 1 << 0,
    DrawIndexed = 1 << 1,
    Dispatch = 1 << 2,
    DispatchMesh = 1 << 3,
    DispatchRays = 1 << 4,
    WorkGraph = 1 << 5,
    VertexBuffer = 1 << 6,
    IndexBuffer = 1 << 7,
    Constants = 1 << 8,
    ConstantBuffer = 1 << 9,
    ShaderResource = 1 << 10,
    UnorderedAccess = 1 << 11,
}

public sealed class IndirectCommands : DeviceCapability
{
    internal IndirectCommands(
        Device device,
        IndirectArgumentTypes argumentTypes,
        uint argumentBufferAlignment,
        uint countBufferAlignment,
        uint maximumCommandCount,
        uint maximumStride)
        : base(device)
    {
        ArgumentTypes = argumentTypes;
        ArgumentBufferAlignment = argumentBufferAlignment;
        CountBufferAlignment = countBufferAlignment;
        MaximumCommandCount = maximumCommandCount;
        MaximumStride = maximumStride;
    }

    public IndirectArgumentTypes ArgumentTypes { get; }
    public uint ArgumentBufferAlignment { get; }
    public uint CountBufferAlignment { get; }
    public uint MaximumCommandCount { get; }
    public uint MaximumStride { get; }
}

public enum IndirectArgumentType : byte
{
    Draw,
    DrawIndexed,
    Dispatch,
    DispatchMesh,
    DispatchRays,
    WorkGraph,
    VertexBuffer,
    IndexBuffer,
    Constants,
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
}

public readonly record struct IndirectArgumentDesc(
    IndirectArgumentType Type,
    uint Slot = 0,
    uint ByteOffset = 0,
    uint ValueCount = 0);

public readonly ref struct IndirectCommandLayoutDesc
{
    public IndirectCommandLayoutDesc(
        ReadOnlySpan<IndirectArgumentDesc> arguments,
        uint stride,
        Pipeline? pipeline = null,
        string? label = null)
    {
        Arguments = arguments;
        Stride = stride;
        Pipeline = pipeline;
        Label = label;
    }

    public ReadOnlySpan<IndirectArgumentDesc> Arguments { get; }
    public uint Stride { get; }
    public Pipeline? Pipeline { get; }
    public string? Label { get; }
}

public abstract class IndirectCommandLayout : DeviceResource
{
    internal IndirectCommandLayout(
        Device device,
        uint stride,
        in PipelineSignature pipelineSignature,
        string? label)
        : base(device, label)
    {
        Stride = stride;
        PipelineSignature = pipelineSignature;
    }

    public uint Stride { get; }
    public PipelineSignature PipelineSignature { get; }
}
