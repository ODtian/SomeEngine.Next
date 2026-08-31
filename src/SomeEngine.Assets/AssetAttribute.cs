namespace SomeEngine.Assets;

/// <summary>
/// Marks one concrete, current-schema asset type stored in <see cref="AssetLoader"/> and
/// loaded as one canonical object. The suffix is the exact authored file suffix for
/// that same type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AssetAttribute : Attribute
{
    public AssetAttribute(string pathSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathSuffix);
        if (!pathSuffix.StartsWith('.'))
            throw new ArgumentException("An asset path suffix must start with '.'.", nameof(pathSuffix));
        PathSuffix = pathSuffix;
    }

    public string PathSuffix { get; }
}
