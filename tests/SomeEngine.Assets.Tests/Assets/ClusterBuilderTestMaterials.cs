using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

internal static class ClusterBuilderTestMaterials
{
    internal static IReadOnlyList<MeshMaterialSlot> Slots(int count = 1)
    {
        var result = new MeshMaterialSlot[count];
        for (int index = 0; index < count; index++)
            result[index] = Slot();
        return result;
    }

    internal static MeshMaterialSlot Slot() => new(Guid.Empty);
}
