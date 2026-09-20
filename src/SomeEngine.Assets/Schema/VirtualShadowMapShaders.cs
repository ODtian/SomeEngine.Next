using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

internal sealed partial class VirtualShadowMapShadersData
{
    internal void Validate(string path)
    {
        string subject = $"Virtual shadow map shader asset '{path}'";
        ShaderRef?[] entries =
        [
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
        ];
        string[] names =
        [
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
    internal static SomeEngine.Serialization.Containers.BinaryDocumentWriter CreateWriter(VirtualShadowMapShadersData fields)
    {
        fields.Validate(fields.Name ?? "");
        return SomeEngine.Serialization.Containers.BinaryDocumentWriter.Create(fields);
    }
}
