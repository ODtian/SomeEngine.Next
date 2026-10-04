using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Names and mesh default weights in source morph target order.</summary>
[AssetBlock("SomeEngine.Assets.Schema.MorphTargetSetBlock"), BinarySerializable]
[BinaryFields(nameof(Names), nameof(DefaultWeights))]
public sealed partial class MorphTargetSetBlock : AssetBlock
{
    public partial IList<string> Names { get; set; }
    public partial IList<float> DefaultWeights { get; set; }
}
