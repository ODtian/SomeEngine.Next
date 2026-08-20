using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class IndirectCommands : DeviceCapability
{
    private readonly ulong _supportedArguments;

    internal IndirectCommands(
        Device device,
        ReadOnlySpan<IndirectArgumentType> supportedArguments,
        uint argumentBufferAlignment,
        uint countBufferAlignment,
        uint maximumCommandCount,
        uint maximumStride)
        : base(device)
    {
        ulong argumentMask = 0;
        foreach (IndirectArgumentType argument in supportedArguments)
        {
            if (!Enum.IsDefined(argument))
                throw new ArgumentOutOfRangeException(nameof(supportedArguments));
            argumentMask |= 1UL << (int)argument;
        }
        _supportedArguments = argumentMask;
        ArgumentBufferAlignment = argumentBufferAlignment;
        CountBufferAlignment = countBufferAlignment;
        MaximumCommandCount = maximumCommandCount;
        MaximumStride = maximumStride;
    }

    public bool Supports(IndirectArgumentType argument) =>
        Enum.IsDefined(argument) &&
        (_supportedArguments & (1UL << (int)argument)) != 0;

    public uint ArgumentBufferAlignment { get; }
    public uint CountBufferAlignment { get; }
    public uint MaximumCommandCount { get; }
    public uint MaximumStride { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct IndirectArgumentDesc(
    IndirectArgumentType Type,
    VariableLayoutReflection Parameters = default,
    uint VertexBufferSlot = 0,
    uint ByteOffset = 0,
    uint ValueCount = 0);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class IndirectCommandLayout : DeviceResource
{
    internal IndirectCommandLayout(
        Device device,
        uint stride,
        Pipeline? pipeline,
        string? label)
        : base(device, label)
    {
        Stride = stride;
        Pipeline = pipeline;
    }

    public uint Stride { get; }
    public Pipeline? Pipeline { get; }
}
