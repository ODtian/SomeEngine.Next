using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Slang inputs that can be acquired asynchronously and compiled into a resident shader.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderSourceBlock"), BinarySerializable, TextSerializable]
[BinaryFields(nameof(SourceNames), nameof(Inputs), nameof(Specializations))]
public sealed partial class ShaderSourceBlock : ShaderInputBlock
{
    /// <summary>Ordered inputs selected from this shader asset's strongly typed blocks.</summary>
    public IList<ShaderInputLocation> Inputs { get => SelectedInputs; set => SelectedInputs = value; }

    internal bool HasSerializedSources
    {
        get
        {
            IReadOnlyCollection<ulong> keys = ContentKeys;
            return SourceNames.Count > 0 && Enumerable.Range(0, SourceNames.Count)
                .All(index => keys.Contains(SerializedSourceKey(index)));
        }
    }

    internal void AddInputs(Guid blockTypeId, int sourceCount)
    {
        for (int index = 0; index < sourceCount; index++) SelectedInputs.Add(new(blockTypeId, index));
        MarkChanged();
    }
}
