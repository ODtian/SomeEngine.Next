using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Editable shader inputs and compile settings, independent from produced backend code.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderSourceBlock"), BinarySerializable, TextSerializable]
[BinaryFields(nameof(SourceFiles), nameof(SelectedEntries), nameof(Specializations), nameof(CookProfile), nameof(Targets))]
public sealed partial class ShaderSourceBlock : AssetPageBlock
{
    public partial IList<string> SourceFiles { get; set; }
    public partial IList<ShaderEntrySelection> SelectedEntries { get; set; }
    public partial IList<string> Specializations { get; set; }
    public partial string CookProfile { get; set; }
    public partial IList<ShaderTarget> Targets { get; set; }

    partial void Initialize() => CookProfile = "default";
}

[BinarySerializable, TextSerializable, BinaryValue]
[BinaryFields(nameof(SourceIndex), nameof(Name))]
public readonly partial record struct ShaderEntrySelection(uint SourceIndex, string Name);
