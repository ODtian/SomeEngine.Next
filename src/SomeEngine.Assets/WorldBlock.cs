using SomeEngine.Serialization;

namespace SomeEngine.Assets;

/// <summary>Content asset references and their absolute streaming coverage.</summary>
[AssetBlock("SomeEngine.Assets.WorldBlock"), BinarySerializable]
[BinaryFields(nameof(Contents))]
public sealed partial class WorldBlock : AssetBlock
{
    public partial ImmutableArray<WorldContent> Contents { get; set; }
    partial void Initialize() => Contents = [];
}
