namespace SomeEngine.Assets;

/// <summary>Generates identity, block directory and file operations. Content belongs to separately declared typed blocks.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AssetAttribute : Attribute
{
    public AssetAttribute(string pathSuffix) => PathSuffix = pathSuffix;
    public string PathSuffix { get; }
    public string? SchemaName { get; set; }
}

