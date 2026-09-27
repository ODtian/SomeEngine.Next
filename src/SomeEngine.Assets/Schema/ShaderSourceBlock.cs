using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Editable shader inputs and compile settings, independent from produced backend code.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderSourceBlock"), BinarySerializable, TextSerializable]
[BinaryFields(nameof(SourceFiles), nameof(Specializations), nameof(CookProfile))]
public sealed partial class ShaderSourceBlock : AssetPageBlock
{
    public partial IList<string> SourceFiles { get; set; }
    public partial IList<string> Specializations { get; set; }
    public partial string CookProfile { get; set; }

    partial void Initialize() => CookProfile = "default";
}
