using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.HeightFieldsBlock"), BinarySerializable]
[BinaryFields(nameof(Fields), nameof(Pages))]
public sealed partial class HeightFieldsBlock : AssetBlock
{
    public partial IList<TerrainHeightField> Fields { get; set; }
    public partial IList<TerrainHeightPage> Pages { get; set; }

    public TerrainPageSource OpenPageSource()
    {
        using HeightFieldsBlock copy = Duplicate();
        return new TerrainPageSource(copy);
    }
}

[AssetBlock("SomeEngine.Assets.Schema.VoxelFieldsBlock"), BinarySerializable]
[BinaryFields(nameof(Fields), nameof(Pages))]
public sealed partial class VoxelFieldsBlock : AssetBlock
{
    public partial IList<TerrainVoxelField> Fields { get; set; }
    public partial IList<TerrainVoxelPage> Pages { get; set; }

    public TerrainPageSource OpenPageSource()
    {
        using VoxelFieldsBlock copy = Duplicate();
        return new TerrainPageSource(copy);
    }
}

[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Id), nameof(Name), nameof(Width), nameof(Depth), nameof(PageSize), nameof(Spacing), nameof(InitialHeight), nameof(Revision))]
public readonly partial record struct TerrainHeightField(int Id, string Name, int Width, int Depth,
    int PageSize, float Spacing, float InitialHeight, ulong Revision);

[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Id), nameof(Name), nameof(PageSize), nameof(Spacing), nameof(InitialValue), nameof(Revision))]
public readonly partial record struct TerrainVoxelField(int Id, string Name, int PageSize, float Spacing,
    float InitialValue, ulong Revision);

[BinarySerializable, BinaryValue, BinaryFields(nameof(FieldId), nameof(X), nameof(Z), nameof(ChunkKey), nameof(Revision), nameof(Id))]
public readonly partial record struct TerrainHeightPage(int FieldId, int X, int Z, ulong ChunkKey, ulong Revision, Guid Id);

[BinarySerializable, BinaryValue, BinaryFields(nameof(FieldId), nameof(X), nameof(Y), nameof(Z), nameof(ChunkKey), nameof(Revision), nameof(Id))]
public readonly partial record struct TerrainVoxelPage(int FieldId, int X, int Y, int Z, ulong ChunkKey, ulong Revision, Guid Id);
