using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[BinaryContract(BinaryCompatibility.ExactSchema)]
[global::SomeEngine.Assets.Asset(".virtualshadow.asset")]
public sealed partial class VirtualShadowMapShaders
{
    public string? AssetGuid { get; set; }

    public string? Name { get; set; }

    public ShaderRef? ResetFrame { get; set; }

    public ShaderRef? MarkPages { get; set; }

    public ShaderRef? MarkCoarsePages { get; set; }

    public ShaderRef? AllocatePages { get; set; }

    public ShaderRef? ClearPages { get; set; }

    public ShaderRef? InitializeState { get; set; }

    public ShaderRef? InvalidateCasters { get; set; }

    public ShaderRef? BuildPagePool { get; set; }

    public ShaderRef? CompactViews { get; set; }

    public ShaderRef? InitializePageHiZ { get; set; }

    public ShaderRef? ReducePageHiZ { get; set; }

    public ShaderRef? DynamicResolution { get; set; }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        string subject = $"Virtual shadow map shader asset '{path}'";
        ShaderRef?[] entries =
        [
            ResetFrame,
            MarkPages,
            MarkCoarsePages,
            AllocatePages,
            ClearPages,
            InitializeState,
            InvalidateCasters,
            BuildPagePool,
            CompactViews,
            InitializePageHiZ,
            ReducePageHiZ,
            DynamicResolution,
        ];
        string[] names =
        [
            nameof(ResetFrame),
            nameof(MarkPages),
            nameof(MarkCoarsePages),
            nameof(AllocatePages),
            nameof(ClearPages),
            nameof(InitializeState),
            nameof(InvalidateCasters),
            nameof(BuildPagePool),
            nameof(CompactViews),
            nameof(InitializePageHiZ),
            nameof(ReducePageHiZ),
            nameof(DynamicResolution),
        ];
        var result = new List<AssetGuid>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            AssetGuid shader = ShaderRef.Require(
                entries[index],
                subject,
                names[index],
                ShaderStage.Compute);
            if (!result.Contains(shader))
                result.Add(shader);
        }
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }
}
