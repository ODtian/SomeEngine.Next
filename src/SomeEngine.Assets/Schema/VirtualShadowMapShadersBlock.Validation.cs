using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public sealed partial class VirtualShadowMapShadersBlock
{
    internal void Validate(string path)
    {
        string subject = $"Virtual shadow map shader asset '{path}'";
        ShaderRef?[] entries =
        [
            ResetReceiverMasks,
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
            nameof(ResetReceiverMasks),
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
