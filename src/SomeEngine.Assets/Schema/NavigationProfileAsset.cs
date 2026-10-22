using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".navigation-profile.asset", SchemaName = "SomeEngine.Assets.Schema.NavigationProfile"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class NavigationProfileAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
