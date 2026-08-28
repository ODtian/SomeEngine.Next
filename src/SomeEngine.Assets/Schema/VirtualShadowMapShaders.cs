using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[BinaryContract(BinaryCompatibility.ExactSchema)]
[global::SomeEngine.Assets.Asset(".virtualshadow.asset")]
public sealed partial class VirtualShadowMapShaders
{
    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public ShaderRef? MarkPages { get; set; }

    public ShaderRef? AllocatePages { get; set; }

    public ShaderRef? ClearPages { get; set; }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        string owner = $"Virtual shadow map shader asset '{path}'";
        AssetGuid mark = ShaderRef.Require(
            MarkPages,
            owner,
            nameof(MarkPages),
            ShaderStage.Compute);
        AssetGuid allocate = ShaderRef.Require(
            AllocatePages,
            owner,
            nameof(AllocatePages),
            ShaderStage.Compute);
        AssetGuid clear = ShaderRef.Require(
            ClearPages,
            owner,
            nameof(ClearPages),
            ShaderStage.Compute);
        var result = new List<AssetGuid>(3) { mark };
        if (!result.Contains(allocate)) result.Add(allocate);
        if (!result.Contains(clear)) result.Add(clear);
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }
}
