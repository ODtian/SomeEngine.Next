using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A learned animation model identity, with content supplied by concrete model blocks.</summary>
[Asset(".animation-model.asset", SchemaName = "SomeEngine.Assets.Schema.AnimationModel")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.AnimationModel")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class AnimationModelAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
