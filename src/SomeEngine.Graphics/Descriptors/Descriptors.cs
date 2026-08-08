using SlangShaderSharp;

namespace SomeEngine.Graphics;

public enum DescriptorTableType : byte
{
    Resource,
    Sampler,
}

public abstract class DescriptorTable : DeviceResource
{
    internal DescriptorTable(
        Device device,
        DescriptorTableType type,
        uint count,
        string? label)
        : base(device, label)
    {
        Type = type;
        Count = count;
    }

    public DescriptorTableType Type { get; }
    public uint Count { get; }
}

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
        ReferenceEquals(_value, other._value) &&
        ArrayElement == other.ArrayElement;

    public override bool Equals(object? obj) => obj is ResourceBinding other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Type, _value, ArrayElement);
    public static bool operator ==(ResourceBinding left, ResourceBinding right) => left.Equals(right);
    public static bool operator !=(ResourceBinding left, ResourceBinding right) => !left.Equals(right);
}

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
