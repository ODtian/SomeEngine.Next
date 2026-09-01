using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public readonly record struct MeshMaterialSlot(
    AssetGuid MaterialGuid,
    MaterialDeformCompatibility DeformCompatibility);

