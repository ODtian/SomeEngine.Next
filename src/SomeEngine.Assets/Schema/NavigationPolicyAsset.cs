using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".navigation-policy.asset", SchemaName = "SomeEngine.Assets.Schema.NavigationPolicy"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class NavigationPolicyAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
