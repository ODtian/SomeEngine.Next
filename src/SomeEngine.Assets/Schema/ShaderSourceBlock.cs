using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Slang inputs that can be acquired asynchronously and compiled into a resident shader.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderSourceBlock"), BinarySerializable, TextSerializable]
[BinaryFields(nameof(SourceNames))]
public sealed partial class ShaderSourceBlock : AssetBlock
{
    public partial IList<string> SourceNames { get; set; }
}
