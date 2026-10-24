using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Material references and named field inputs. Material indices address the Materials collection.</summary>
[AssetBlock("SomeEngine.Assets.Schema.TerrainMaterialBlock"), BinarySerializable]
[BinaryFields(nameof(Materials), nameof(HeightSources), nameof(VoxelSources))]
public sealed partial class TerrainMaterialBlock : AssetBlock
{
    public partial IList<AssetId<MaterialAsset>> Materials { get; set; }
    public partial IList<TerrainMaterialHeightSource> HeightSources { get; set; }
    public partial IList<TerrainMaterialVoxelSource> VoxelSources { get; set; }
}

[BinarySerializable, BinaryValue, BinaryFields(nameof(MaterialIndex), nameof(Input), nameof(FieldId))]
public readonly partial record struct TerrainMaterialHeightSource(int MaterialIndex, string Input, int FieldId);

[BinarySerializable, BinaryValue, BinaryFields(nameof(MaterialIndex), nameof(Input), nameof(FieldId))]
public readonly partial record struct TerrainMaterialVoxelSource(int MaterialIndex, string Input, int FieldId);
