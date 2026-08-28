namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".virtualshadow.asset")]
public sealed partial class VirtualShadowMapAlgorithm
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        AssetGuid mark = ComputeKernelRefValidation.Require(MarkPages, path, nameof(MarkPages));
        AssetGuid allocate = ComputeKernelRefValidation.Require(
            AllocatePages,
            path,
            nameof(AllocatePages));
        AssetGuid clear = ComputeKernelRefValidation.Require(ClearPages, path, nameof(ClearPages));
        var result = new List<AssetGuid>(3) { mark };
        if (!result.Contains(allocate)) result.Add(allocate);
        if (!result.Contains(clear)) result.Add(clear);
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }
}
