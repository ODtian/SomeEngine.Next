namespace SomeEngine.Resources;

/// <summary>
/// Immutable synchronous-use view of direct Slang objects and their generated or saved code.
/// Asset and block access completes before this object is returned.
/// </summary>
[SomeEngine.Serialization.AssetUse]
public partial class Shader
{
    public string Name { get; }
}
