namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".shader.asset")]
public partial class Shader
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path) => [];
}
