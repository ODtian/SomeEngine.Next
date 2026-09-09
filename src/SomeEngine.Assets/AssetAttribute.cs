namespace SomeEngine.Assets;

/// <summary>Associates disk fields with their domain type at compile time.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AssetAttribute(Type type, string pathSuffix) : Attribute
{
    public Type Type { get; } = type;
    public string PathSuffix { get; } = pathSuffix;
}
