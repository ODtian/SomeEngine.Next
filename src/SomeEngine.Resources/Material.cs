using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

/// <summary>A material has concrete properties and actual shader and texture references.</summary>
/// <remarks>Business changes fields between CPU frame recording and the next extraction.</remarks>
[SomeEngine.Serialization.AssetUse]
public abstract partial class Material
{
    private ulong[]? _changes;
    private bool _changingProperties;
    private Shader? _shader;
    private MaterialState _state = MaterialState.Default;
    private static long _nextMaterialId;
    private readonly long _materialId = Interlocked.Increment(ref _nextMaterialId);
    public long MaterialId => _materialId;
    public string? Name { get; set; }
    public Shader? Shader
    {
        get => _shader;
        set
        {
            if (ReferenceEquals(_shader, value)) return;
            _shader = value;
            if (!_changingProperties) Revision++;
        }
    }
    public ulong Revision { get; private set; } = 1;
    public MaterialState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            if (!_changingProperties) Revision++;
        }
    }
    public abstract IReadOnlyList<MaterialPropertyDefinition> Properties { get; }
    public int PropertyCount => Properties.Count;
    /// <summary>Conservative world-space displacement distance, derived from this material's geometric parameters.</summary>
    public virtual float BoundsExpansion => 0f;

    /// <summary>Finds the first property whose name matches using ordinal comparison.</summary>
    public virtual uint? FindPropertyIndex(string? name)
    {
        IReadOnlyList<MaterialPropertyDefinition> properties = Properties;
        ArgumentNullException.ThrowIfNull(properties);
        for (uint index = 0; index < properties.Count; index++)
        {
            if (string.Equals(properties[checked((int)index)].Name, name, StringComparison.Ordinal))
                return index;
        }
        return null;
    }

    /// <summary>
    /// Resolves each resource to the first same-named property of this material.
    /// A used resource selects its name; every resource with that name is then validated and mapped,
    /// including unused resources and separate array elements. Resource use is handled by the caller.
    /// </summary>
    public uint?[] ResolveResourceProperties(ReadOnlySpan<ShaderParameterResource> resources)
    {
        var materialResources = new Dictionary<string, (byte Kind, uint Property)>(StringComparer.Ordinal);
        foreach (ref readonly ShaderParameterResource resource in resources)
        {
            string resourceName = resource.Name;
            if (!resource.IsUsed || resource.Type == ResourceBindingType.None ||
                FindPropertyIndex(resourceName) is not uint property)
            {
                continue;
            }
            materialResources.TryAdd(resource.Name, (ResourceKind(resource.Type), property));
        }

        foreach (var (name, (kind, _)) in materialResources)
        {
            foreach (ref readonly ShaderParameterResource resource in resources)
            {
                if (!string.Equals(resource.Name, name, StringComparison.Ordinal))
                    continue;
                bool compatible = kind switch
                {
                    0 => resource.Type is ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav,
                    1 => resource.Type == ResourceBindingType.Sampler,
                    2 => resource.Type is ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav or
                        ResourceBindingType.ConstantBuffer,
                    3 => resource.Type == ResourceBindingType.AccelerationStructure,
                    _ => false,
                };
                if (!compatible)
                {
                    throw new InvalidDataException(
                        $"Material resource '{name}' cooked kind {kind} is incompatible with " +
                        $"{resource.Type} in linked reflection.");
                }
            }
        }

        var result = new uint?[resources.Length];
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ShaderParameterResource resource = ref resources[resourceIndex];
            if (!materialResources.TryGetValue(resource.Name, out var resourceProperty))
                continue;
            result[resourceIndex] = resourceProperty.Property;
        }
        return result;
    }

    private static byte ResourceKind(ResourceBindingType type) => type switch
    {
        ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav => 0,
        ResourceBindingType.Sampler => 1,
        ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav or
            ResourceBindingType.ConstantBuffer => 2,
        ResourceBindingType.AccelerationStructure => 3,
        _ => throw new InvalidDataException($"Resource type {type} cannot be a material resource."),
    };

    /// <summary>Reads a concrete device-resource property as the expected binding type.</summary>
    /// <remarks>
    /// The returned binding borrows the property's
    /// resource; it does not retain or realize it. Texture and sampler preparation belongs to the
    /// render device and is not performed by this method.
    /// </remarks>
    public ResourceBinding GetDeviceResourceBinding(uint property, ResourceBindingType type) => type switch
    {
        ResourceBindingType.BufferSrv => ResourceBinding.ReadOnlyBuffer(RequireDeviceResource<BufferSrv>(property)),
        ResourceBindingType.BufferUav => ResourceBinding.WritableBuffer(RequireDeviceResource<BufferUav>(property)),
        ResourceBindingType.ConstantBuffer => ResourceBinding.ConstantBuffer(RequireDeviceResource<BufferCbv>(property)),
        ResourceBindingType.AccelerationStructure => ResourceBinding.AccelerationStructure(RequireDeviceResource<AccelerationStructureSrv>(property)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The binding type must describe a concrete device-resource property."),
    };

    private T RequireDeviceResource<T>(uint property) where T : class
    {
        object? value = GetDeviceResource(property);
        RequireDeviceResource(value is T,
            $"Material '{Name}' property {property} must contain a {typeof(T).Name}.");
        return (T)value!;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void RequireDeviceResource(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    protected void SetProperty<T>(ref T field, T value, uint index)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _changes ??= new ulong[PropertyCount];
        _changes[index]++;
        if (!_changingProperties) Revision++;
    }

    public ulong GetPropertyRevision(uint index) => _changes?[index] ?? 0;
    public virtual Texture? GetTexture(uint index) => null;
    public virtual DeviceResource? GetDeviceResource(uint index) => null;
    public virtual SamplerDesc GetSampler(uint index) => default;
    public virtual Vector4 GetFloatComponents(uint index) => default;
    public virtual uint GetInteger(uint index) => 0;

    protected void BeginContent() => _changingProperties = true;
    protected void FinishContent() => _changingProperties = false;
    protected void BeginUpdate()
    {
        _changingProperties = true;
    }
    protected void FinishUpdate()
    {
        _changingProperties = false;
        Revision++;
    }
}

public readonly record struct MaterialPropertyDefinition(uint Index, string Name, Type ValueType);
