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
    public abstract IReadOnlyList<MaterialPropertyDefinition> Properties { get; }
    public int PropertyCount => Properties.Count;
    /// <summary>Conservative world-space displacement distance, derived from this material's geometric parameters.</summary>
    public virtual float BoundsExpansion => 0f;

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
