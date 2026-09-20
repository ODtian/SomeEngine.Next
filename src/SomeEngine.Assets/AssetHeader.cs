using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>The public root of a block asset, available without installing its business codecs.</summary>
[BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class AssetHeader
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string AssetType { get; set; } = "";
    public ImmutableArray<AssetBlockDescription> BlockDescriptions { get; set; } = [];
    public static BinaryWireTypeDescriptor WireType => new(TypeId, SchemaFingerprint, Compatibility, SchemaEpoch);
}
