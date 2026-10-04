using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public sealed record MorphTargetSetSettings;

[JsonSerializable(typeof(MorphTargetSetSettings))]
public sealed partial class MorphTargetSetSettingsJson : JsonSerializerContext;

public sealed class MorphTargetSetSettingsProvider : IAssetImporterSettings<MorphTargetSetSettings>
{
    public MorphTargetSetSettings DefaultSettings => new();
    public JsonTypeInfo<MorphTargetSetSettings> TypeInfo => MorphTargetSetSettingsJson.Default.MorphTargetSetSettings;
}

/// <summary>Imports mesh morph target names and default weights independently of vertex encoding.</summary>
[AssetImporter("adf30b58-0451-4dcb-b53e-5b9422f6ea49", typeof(MorphTargetSetBlock),
    SettingsProvider = typeof(MorphTargetSetSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class MorphTargetSetImporter : IAssetImporter<Mesh, MorphTargetSetSettings>
{
    public static readonly Guid Id = new("adf30b58-0451-4dcb-b53e-5b9422f6ea49");

    public ValueTask ImportAsync(Mesh source, MorphTargetSetSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = source.Primitives[0].MorphTargetsCount;
        if (count == 0) return ValueTask.CompletedTask;
        JsonArray? sourceNames = source.Extras?["targetNames"]?.AsArray();
        IReadOnlyList<float> sourceWeights = source.MorphWeights;
        if (sourceNames is not null && sourceNames.Count != count)
            throw new InvalidDataException("mesh.extras.targetNames must contain one name per morph target.");
        if (sourceWeights.Count != 0 && sourceWeights.Count != count)
            throw new InvalidDataException("mesh.weights must contain one weight per morph target.");

        var names = new string[count];
        var weights = new float[count];
        for (int target = 0; target < count; target++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            names[target] = sourceNames is null ? string.Empty : sourceNames[target]!.GetValue<string>();
            weights[target] = sourceWeights.Count == 0 ? 0 : sourceWeights[target];
        }
        var block = new MorphTargetSetBlock { Names = names, DefaultWeights = weights };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
