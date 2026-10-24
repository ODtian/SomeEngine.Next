using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A skeleton identity whose content is supplied by animation blocks.</summary>
[Asset(".skeleton.asset", SchemaName = "SomeEngine.Assets.Schema.Skeleton")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.Skeleton")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class SkeletonAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
