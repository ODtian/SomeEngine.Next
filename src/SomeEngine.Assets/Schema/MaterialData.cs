namespace SomeEngine.Assets.Schema;

/// <summary>
/// Material property values captured at one extraction boundary. Asset references identify
/// separately managed content; this data never exposes the editable source material.
/// </summary>
public sealed class MaterialData
{
    private readonly object?[] _values;
    private readonly ulong[] _propertyRevisions;

    internal MaterialData(
        long materialId,
        ulong revision,
        string? name,
        string? shaderGuid,
        Shader? shader,
        MaterialSlotDefinition[] properties,
        object?[] values,
        ulong[] propertyRevisions)
    {
        MaterialId = materialId;
        Revision = revision;
        Name = name;
        ShaderGuid = shaderGuid;
        ShaderAsset = shader;
        Properties = Array.AsReadOnly(properties);
        _values = values;
        _propertyRevisions = propertyRevisions;
    }

    public long MaterialId { get; }
    public ulong Revision { get; }
    public string? Name { get; }
    public string? ShaderGuid { get; }
    public Shader? ShaderAsset { get; }
    public IReadOnlyList<MaterialSlotDefinition> Properties { get; }
    public int PropertyCount => _values.Length;
    public object? GetValue(uint property) => _values[checked((int)property)];
    public ulong GetPropertyRevision(uint property) => _propertyRevisions[checked((int)property)];
}
