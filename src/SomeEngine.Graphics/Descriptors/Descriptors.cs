using System.Runtime.CompilerServices;
using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum DescriptorTableType : byte
{
    Resource,
    Sampler,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DescriptorSlotDesc(
    ResourceBindingType Type,
    Format? Format = null,
    uint StructureStride = 0,
    TextureViewDimension? TextureDimension = null,
    bool HasCounter = false,
    TextureAspects Aspects = TextureAspects.Color);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be copied and shared.</para>
/// <para><b>Ownership:</b> Pure value. Identity is the exact <see cref="DescriptorTable"/> reference plus <see cref="Value"/>; the value owns no table or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state. Disposing its table makes the index unusable for receiver operations, and the copied numeric value is never reinterpreted as an index into another table.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly struct DescriptorIndex : IEquatable<DescriptorIndex>
{
    private readonly DescriptorTable? _table;

    internal DescriptorIndex(DescriptorTable table, uint value)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        Value = value;
    }

    public uint Value { get; }
    public bool IsValid => _table is not null;

    public bool Equals(DescriptorIndex other) =>
        ReferenceEquals(_table, other._table) && Value == other.Value;

    public override bool Equals(object? obj) => obj is DescriptorIndex other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        _table is null ? 0 : RuntimeHelpers.GetHashCode(_table),
        Value);
    public static bool operator ==(DescriptorIndex left, DescriptorIndex right) => left.Equals(right);
    public static bool operator !=(DescriptorIndex left, DescriptorIndex right) => !left.Equals(right);
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class DescriptorTable : DeviceResource
{
    private const uint MaximumStructuredBufferStride = 2_048;

    private readonly DescriptorSlotDesc[] _slots;

    internal DescriptorTable(
        Device device,
        DescriptorTableType type,
        uint nodeIndex,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label)
        : base(device ?? throw new ArgumentNullException(nameof(device)), label)
    {
        if (slots.IsEmpty)
            throw new ArgumentException("A DescriptorTable requires at least one typed slot.", nameof(slots));
        _slots = slots.ToArray();
        bool samplers = type == DescriptorTableType.Sampler;
        foreach (DescriptorSlotDesc slot in _slots)
        {
            if (!Enum.IsDefined(slot.Type) || slot.Type == ResourceBindingType.None)
                throw new ArgumentOutOfRangeException(nameof(slots));
            if ((slot.Type == ResourceBindingType.Sampler) != samplers)
            {
                throw new ArgumentException(
                    "A DescriptorTable slot must match the table heap type.",
                    nameof(slots));
            }
            ValidateSlot(slot, nameof(slots));
        }
        Type = type;
        NodeIndex = nodeIndex;
        Count = checked((uint)_slots.Length);
    }

    public DescriptorTableType Type { get; }
    public uint NodeIndex { get; }
    public uint Count { get; }
    public ReadOnlySpan<DescriptorSlotDesc> Slots => _slots;

    public DescriptorSlotDesc GetSlotDesc(uint slot)
    {
        if (slot >= Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return _slots[checked((int)slot)];
    }

    internal ResourceBindingType GetSlotType(uint slot) => GetSlotDesc(slot).Type;

    private static void ValidateSlot(in DescriptorSlotDesc slot, string parameterName)
    {
        switch (slot.Type)
        {
            case ResourceBindingType.ConstantBuffer:
            case ResourceBindingType.Sampler:
            case ResourceBindingType.AccelerationStructure:
                ValidateUnshapedSlot(slot, parameterName);
                break;
            case ResourceBindingType.BufferSrv:
            case ResourceBindingType.BufferUav:
                ValidateBufferSlot(slot, parameterName);
                break;
            case ResourceBindingType.TextureSrv:
            case ResourceBindingType.TextureUav:
                ValidateTextureSlot(slot, parameterName);
                break;
            default:
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateUnshapedSlot(
        in DescriptorSlotDesc slot,
        string parameterName)
    {
        if (slot.Format.HasValue ||
            slot.StructureStride != 0 ||
            slot.TextureDimension.HasValue ||
            slot.HasCounter)
        {
            throw new ArgumentException(
                "The descriptor slot contains fields that do not apply to its type.",
                parameterName);
        }
    }

    private static void ValidateBufferSlot(
        in DescriptorSlotDesc slot,
        string parameterName)
    {
        if (slot.TextureDimension.HasValue ||
            slot.Format.HasValue && slot.StructureStride != 0)
        {
            throw new ArgumentException(
                "A Buffer slot cannot combine texture or typed/structured shapes.",
                parameterName);
        }

        uint stride = slot.StructureStride;
        if (stride != 0 && ((stride & 3) != 0 || stride > MaximumStructuredBufferStride))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"A structured Buffer stride must be a four-byte multiple no greater than {MaximumStructuredBufferStride}.");
        }

        if (slot.HasCounter &&
            (slot.Type != ResourceBindingType.BufferUav || stride == 0))
        {
            throw new ArgumentException(
                "Only a structured Buffer UAV slot may carry a counter.",
                parameterName);
        }
    }

    private static void ValidateTextureSlot(
        in DescriptorSlotDesc slot,
        string parameterName)
    {
        if (!slot.Format.HasValue ||
            !slot.TextureDimension.HasValue ||
            slot.StructureStride != 0 ||
            slot.HasCounter)
        {
            throw new ArgumentException(
                "A Texture slot requires Format and TextureDimension only.",
                parameterName);
        }

        if (!Enum.IsDefined(slot.TextureDimension.Value))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum ResourceBindingType : byte
{
    None,
    ConstantBuffer,
    BufferSrv,
    BufferUav,
    TextureSrv,
    TextureUav,
    Sampler,
    AccelerationStructure,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly struct ResourceBinding : IEquatable<ResourceBinding>
{
    private readonly GraphicsObject? _value;

    private ResourceBinding(ResourceBindingType type, GraphicsObject? value)
    {
        Type = type;
        _value = value;
    }

    public ResourceBindingType Type { get; }
    public GraphicsObject? Value => _value;
    public bool IsNull => _value is null;

    public static ResourceBinding Null(ResourceBindingType type)
    {
        if (type == ResourceBindingType.None)
            return default;
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        return new ResourceBinding(type, null);
    }

    public static ResourceBinding ConstantBuffer(BufferCbv value) =>
        new(ResourceBindingType.ConstantBuffer, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding ReadOnlyBuffer(BufferSrv value) =>
        new(ResourceBindingType.BufferSrv, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding WritableBuffer(BufferUav value) =>
        new(ResourceBindingType.BufferUav, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding SampledTexture(TextureSrv value) =>
        new(ResourceBindingType.TextureSrv, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding StorageTexture(TextureUav value) =>
        new(ResourceBindingType.TextureUav, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding SampledWith(Sampler value) =>
        new(ResourceBindingType.Sampler, value ?? throw new ArgumentNullException(nameof(value)));

    public static ResourceBinding AccelerationStructure(
        AccelerationStructureSrv value) =>
        new(ResourceBindingType.AccelerationStructure, value ?? throw new ArgumentNullException(nameof(value)));

    public bool Equals(ResourceBinding other) =>
        Type == other.Type && ReferenceEquals(_value, other._value);

    public override bool Equals(object? obj) => obj is ResourceBinding other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        Type,
        _value is null ? 0 : RuntimeHelpers.GetHashCode(_value));
    public static bool operator ==(ResourceBinding left, ResourceBinding right) => left.Equals(right);
    public static bool operator !=(ResourceBinding left, ResourceBinding right) => !left.Equals(right);
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct ParameterBlockBindings
{
    public ParameterBlockBindings(
        VariableLayoutReflection layout,
        ReadOnlySpan<ResourceBinding> resources,
        ReadOnlySpan<byte> ordinaryData)
    {
        Layout = layout;
        Resources = resources;
        OrdinaryData = ordinaryData;
    }

    public VariableLayoutReflection Layout { get; }
    public ReadOnlySpan<ResourceBinding> Resources { get; }
    public ReadOnlySpan<byte> OrdinaryData { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe for concurrent binding and updates; each binding operation observes one immutable published generation. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class PersistentParameterBindings : DeviceResource
{
    internal PersistentParameterBindings(
        Device device,
        VariableLayoutReflection layout,
        string? label)
        : base(device, label)
    {
        Layout = layout;
    }

    public VariableLayoutReflection Layout { get; }
}
