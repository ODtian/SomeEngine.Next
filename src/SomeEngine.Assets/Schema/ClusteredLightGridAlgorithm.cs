namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".clusteredlighting.asset")]
public sealed partial class ClusteredLightGridAlgorithm
{
    internal AssetGuid GetShaderDependency(string path) =>
        ComputeKernelRefValidation.Require(BuildGrid, path, nameof(BuildGrid));

    internal IReadOnlyList<AssetGuid> GetDependencies(string path) =>
        [GetShaderDependency(path)];
}
