using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Project-selected material references in geometry region order. Empty entries remain unbound.</summary>
[AssetBlock("SomeEngine.Assets.Schema.MeshMaterialsBlock"), BinarySerializable, BinaryFields(nameof(Materials))]
public sealed partial class MeshMaterialsBlock : AssetBlock
{
    public partial IList<AssetId<MaterialAsset>> Materials { get; set; }
}
