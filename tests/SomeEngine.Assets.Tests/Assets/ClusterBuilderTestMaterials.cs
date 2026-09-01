using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

internal static class ClusterBuilderTestMaterials
{
    private static readonly AssetGuid TestShader =
        AssetGuid.Parse("7f7410e4815b4e50996ed2ea62025673");

    internal static IReadOnlyList<MeshMaterialSlot> Slots(int count = 1)
    {
        var result = new MeshMaterialSlot[count];
        for (int index = 0; index < count; index++)
            result[index] = Slot(compatibility: 0);
        return result;
    }

    internal static MeshMaterialSlot Slot(int compatibility) => new(
        AssetGuid.Empty,
        new MaterialDeformCompatibility(
            TestShader,
            $"TestDeform{compatibility}"));
}
