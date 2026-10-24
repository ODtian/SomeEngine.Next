using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>An animation clip identity whose content is supplied by animation blocks.</summary>
[Asset(".animation-clip.asset", SchemaName = "SomeEngine.Assets.Schema.AnimationClip")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.Schema.AnimationClip")]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class AnimationClipAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
