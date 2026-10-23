using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A bone-mask identity whose content is supplied by animation blocks.</summary>
[Asset(".bone-mask.asset", SchemaName = "SomeEngine.Assets.Schema.BoneMask")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.BoneMask")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class BoneMaskAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
