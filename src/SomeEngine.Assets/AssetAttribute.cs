namespace SomeEngine.Assets;

/// <summary>Generates editable asset fields from a domain type's declared data properties.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AssetAttribute : Attribute
{
    public AssetAttribute(string pathSuffix) => PathSuffix = pathSuffix;
    public string PathSuffix { get; }
    public string? SchemaName { get; set; }
}

/// <summary>Marks a GUID that identifies another asset, rather than an ordinary GUID value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AssetReferenceAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}
