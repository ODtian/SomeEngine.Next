namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".texture.asset")]
public partial class Texture
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path) => [];
}
