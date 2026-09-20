using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public sealed partial class VirtualShadowMapShadersBlock
{
    internal void Validate(string path)
    {
        string subject = $"Virtual shadow map shader asset '{path}'";
        ShaderRef?[] entries =
        [
            ResetFrame, DynamicResolution, MarkPages,
            MarkCoarsePages,
            AllocatePages,
            ClearPages,
            InitializeState,
            InvalidateCasters,
            BuildPagePool,
            CompactViews,
            InitializePageHiZ,
            ReducePageHiZ,
        ];
        string[] names =
        [
            nameof(ResetFrame), nameof(DynamicResolution), nameof(MarkPages),
            nameof(MarkCoarsePages),
            nameof(AllocatePages),
            nameof(ClearPages),
            nameof(InitializeState),
            nameof(InvalidateCasters),
            nameof(BuildPagePool),
            nameof(CompactViews),
            nameof(InitializePageHiZ),
            nameof(ReducePageHiZ),
        ];
        for (int index = 0; index < entries.Length; index++)
        {
            ShaderRef.Require(
                entries[index],
                subject,
                names[index],
                ShaderStage.Compute);
        }
    }
}
