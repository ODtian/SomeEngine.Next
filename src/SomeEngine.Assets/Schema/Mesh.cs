namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".mesh.asset")]
public partial class Mesh
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path) => [];
}
