using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".navigation-flow.asset", SchemaName = "SomeEngine.Assets.Schema.NavigationFlow"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class NavigationFlowAsset : Asset
{
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get => base.BlockDescriptions;
        private set => base.BlockDescriptions = value;
    }
}
