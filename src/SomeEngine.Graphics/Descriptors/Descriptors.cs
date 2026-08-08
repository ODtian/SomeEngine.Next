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
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class DescriptorTable : DeviceResource
{
    private readonly ResourceBindingType[] _slotTypes;

    internal DescriptorTable(
        Device device,
        ReadOnlySpan<ResourceBindingType> slotTypes,
        string? label)
        : base(device, label)
    {
        if (slotTypes.IsEmpty)
            throw new ArgumentException("A DescriptorTable requires at least one typed slot.", nameof(slotTypes));
        _slotTypes = slotTypes.ToArray();
        bool samplers = _slotTypes[0] == ResourceBindingType.Sampler;
        foreach (ResourceBindingType slotType in _slotTypes)
        {
            if (!Enum.IsDefined(slotType) || slotType == ResourceBindingType.None)
                throw new ArgumentOutOfRangeException(nameof(slotTypes));
            if ((slotType == ResourceBindingType.Sampler) != samplers)
            {
                throw new ArgumentException(
                    "A DescriptorTable cannot mix Resource and Sampler slots.",
                    nameof(slotTypes));
            }
        }
        Type = samplers ? DescriptorTableType.Sampler : DescriptorTableType.Resource;
        Count = checked((uint)_slotTypes.Length);
    }

    public DescriptorTableType Type { get; }
    public uint Count { get; }
    public ReadOnlySpan<ResourceBindingType> SlotTypes => _slotTypes;

    public ResourceBindingType GetSlotType(uint slot)
    {
        if (slot >= Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return _slotTypes[checked((int)slot)];
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
    private readonly object? _value;

    private ResourceBinding(ResourceBindingType type, object? value, uint arrayElement)
    {
        Type = type;
        _value = value;
        ArrayElement = arrayElement;
    }

    public ResourceBindingType Type { get; }
    public uint ArrayElement { get; }
    public object? Value => _value;
    public bool IsNull => _value is null;

    public static ResourceBinding Null(ResourceBindingType type, uint arrayElement = 0)
    {
        if (type == ResourceBindingType.None)
            return default;
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        return new ResourceBinding(type, null, arrayElement);
    }

    public static ResourceBinding ConstantBuffer(BufferCbv value, uint arrayElement = 0) =>
        new(ResourceBindingType.ConstantBuffer, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding ReadOnlyBuffer(BufferSrv value, uint arrayElement = 0) =>
        new(ResourceBindingType.BufferSrv, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding WritableBuffer(BufferUav value, uint arrayElement = 0) =>
        new(ResourceBindingType.BufferUav, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding SampledTexture(TextureSrv value, uint arrayElement = 0) =>
        new(ResourceBindingType.TextureSrv, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding StorageTexture(TextureUav value, uint arrayElement = 0) =>
        new(ResourceBindingType.TextureUav, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding SampledWith(Sampler value, uint arrayElement = 0) =>
        new(ResourceBindingType.Sampler, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public static ResourceBinding AccelerationStructure(
        AccelerationStructureSrv value,
        uint arrayElement = 0) =>
        new(ResourceBindingType.AccelerationStructure, value ?? throw new ArgumentNullException(nameof(value)), arrayElement);

    public bool Equals(ResourceBinding other) =>
        Type == other.Type &&
        ValuesEqual(Type, _value, other._value) &&
        ArrayElement == other.ArrayElement;

    public override bool Equals(object? obj) => obj is ResourceBinding other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        Type,
        ValueHashCode(Type, _value),
        ArrayElement);
    public static bool operator ==(ResourceBinding left, ResourceBinding right) => left.Equals(right);
    public static bool operator !=(ResourceBinding left, ResourceBinding right) => !left.Equals(right);

    private static bool ValuesEqual(
        ResourceBindingType type,
        object? left,
        object? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;

        return type switch
        {
            ResourceBindingType.ConstantBuffer =>
                left is BufferCbv leftView && right is BufferCbv rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource) &&
                leftView.Description.Range.Resolve(leftView.Resource.Info.Size) ==
                    rightView.Description.Range.Resolve(rightView.Resource.Info.Size),
            ResourceBindingType.BufferSrv =>
                left is BufferSrv leftView && right is BufferSrv rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource) &&
                leftView.Description.Range.Resolve(leftView.Resource.Info.Size) ==
                    rightView.Description.Range.Resolve(rightView.Resource.Info.Size) &&
                leftView.Description.Format == rightView.Description.Format &&
                leftView.Description.StructureStride == rightView.Description.StructureStride,
            ResourceBindingType.BufferUav =>
                left is BufferUav leftView && right is BufferUav rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource) &&
                leftView.Description.Range.Resolve(leftView.Resource.Info.Size) ==
                    rightView.Description.Range.Resolve(rightView.Resource.Info.Size) &&
                leftView.Description.Format == rightView.Description.Format &&
                leftView.Description.StructureStride == rightView.Description.StructureStride &&
                ReferenceEquals(
                    leftView.Description.CounterBuffer,
                    rightView.Description.CounterBuffer) &&
                leftView.Description.CounterOffset == rightView.Description.CounterOffset,
            ResourceBindingType.TextureSrv =>
                left is TextureSrv leftView && right is TextureSrv rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource) &&
                leftView.Description.Range == rightView.Description.Range &&
                leftView.Description.Format == rightView.Description.Format &&
                leftView.Description.Dimension == rightView.Description.Dimension,
            ResourceBindingType.TextureUav =>
                left is TextureUav leftView && right is TextureUav rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource) &&
                leftView.Description.Range == rightView.Description.Range &&
                leftView.Description.Format == rightView.Description.Format &&
                leftView.Description.Dimension == rightView.Description.Dimension,
            ResourceBindingType.Sampler =>
                left is Sampler leftSampler && right is Sampler rightSampler &&
                SamplersEqual(leftSampler.Description, rightSampler.Description),
            ResourceBindingType.AccelerationStructure =>
                left is AccelerationStructureSrv leftView &&
                right is AccelerationStructureSrv rightView &&
                ReferenceEquals(leftView.Resource, rightView.Resource),
            _ => false,
        };
    }

    private static bool SamplersEqual(in SamplerDesc left, in SamplerDesc right) =>
        left.MinFilter == right.MinFilter &&
        left.MagFilter == right.MagFilter &&
        left.MipFilter == right.MipFilter &&
        left.AddressU == right.AddressU &&
        left.AddressV == right.AddressV &&
        left.AddressW == right.AddressW &&
        left.MipLodBias.Equals(right.MipLodBias) &&
        left.MaximumAnisotropy == right.MaximumAnisotropy &&
        left.Comparison == right.Comparison &&
        left.BorderColor.X.Equals(right.BorderColor.X) &&
        left.BorderColor.Y.Equals(right.BorderColor.Y) &&
        left.BorderColor.Z.Equals(right.BorderColor.Z) &&
        left.BorderColor.W.Equals(right.BorderColor.W) &&
        left.MinimumLod.Equals(right.MinimumLod) &&
        left.MaximumLod.Equals(right.MaximumLod);

    private static int ValueHashCode(ResourceBindingType type, object? value)
    {
        if (value is null)
            return 0;

        HashCode hash = new();
        switch (type)
        {
            case ResourceBindingType.ConstantBuffer when value is BufferCbv view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                hash.Add(view.Description.Range.Resolve(view.Resource.Info.Size));
                break;
            case ResourceBindingType.BufferSrv when value is BufferSrv view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                hash.Add(view.Description.Range.Resolve(view.Resource.Info.Size));
                hash.Add(view.Description.Format);
                hash.Add(view.Description.StructureStride);
                break;
            case ResourceBindingType.BufferUav when value is BufferUav view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                hash.Add(view.Description.Range.Resolve(view.Resource.Info.Size));
                hash.Add(view.Description.Format);
                hash.Add(view.Description.StructureStride);
                hash.Add(view.Description.CounterBuffer is null
                    ? 0
                    : RuntimeHelpers.GetHashCode(view.Description.CounterBuffer));
                hash.Add(view.Description.CounterOffset);
                break;
            case ResourceBindingType.TextureSrv when value is TextureSrv view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                hash.Add(view.Description.Range);
                hash.Add(view.Description.Format);
                hash.Add(view.Description.Dimension);
                break;
            case ResourceBindingType.TextureUav when value is TextureUav view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                hash.Add(view.Description.Range);
                hash.Add(view.Description.Format);
                hash.Add(view.Description.Dimension);
                break;
            case ResourceBindingType.Sampler when value is Sampler sampler:
                AddSamplerHash(ref hash, sampler.Description);
                break;
            case ResourceBindingType.AccelerationStructure
                when value is AccelerationStructureSrv view:
                hash.Add(RuntimeHelpers.GetHashCode(view.Resource));
                break;
            default:
                hash.Add(RuntimeHelpers.GetHashCode(value));
                break;
        }
        return hash.ToHashCode();
    }

    private static void AddSamplerHash(ref HashCode hash, in SamplerDesc value)
    {
        hash.Add(value.MinFilter);
        hash.Add(value.MagFilter);
        hash.Add(value.MipFilter);
        hash.Add(value.AddressU);
        hash.Add(value.AddressV);
        hash.Add(value.AddressW);
        hash.Add(value.MipLodBias);
        hash.Add(value.MaximumAnisotropy);
        hash.Add(value.Comparison);
        hash.Add(value.BorderColor.X);
        hash.Add(value.BorderColor.Y);
        hash.Add(value.BorderColor.Z);
        hash.Add(value.BorderColor.W);
        hash.Add(value.MinimumLod);
        hash.Add(value.MaximumLod);
    }
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
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
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class PersistentParameterBindings : DeviceResource
{
    private int _status = (int)PersistentParameterBindingsStatus.Unpublished;

    internal PersistentParameterBindings(
        Device device,
        VariableLayoutReflection layout,
        string? label)
        : base(device, label)
    {
        Layout = layout;
    }

    public VariableLayoutReflection Layout { get; }

    public PersistentParameterBindingsStatus Status =>
        IsDisposed
            ? PersistentParameterBindingsStatus.Disposed
            : (PersistentParameterBindingsStatus)Volatile.Read(ref _status);

    internal void MarkPublished() =>
        Interlocked.CompareExchange(
            ref _status,
            (int)PersistentParameterBindingsStatus.Published,
            (int)PersistentParameterBindingsStatus.Unpublished);
}
