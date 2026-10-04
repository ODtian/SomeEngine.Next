using System.Text.Json.Nodes;
using SomeEngine.Assets.Importers;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record SkinBindingSettings;

[JsonSerializable(typeof(SkinBindingSettings))]
public sealed partial class SkinBindingSettingsJson : JsonSerializerContext;

public sealed class SkinBindingSettingsProvider : IAssetImporterSettings<SkinBindingSettings>
{
    public SkinBindingSettings DefaultSettings => new();
    public JsonTypeInfo<SkinBindingSettings> TypeInfo => SkinBindingSettingsJson.Default.SkinBindingSettings;
}

/// <summary>Combines source inverse bind matrices with the selected skeleton's joint correspondence.</summary>
[AssetImporter("c35793df-095e-4bc9-8b04-a027a1a8cc91", typeof(SkinBindingBlock),
    SettingsProvider = typeof(SkinBindingSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class SkinBindingImporter : IAssetImporter<Skin, SkinBindingSettings>
{
    public static readonly Guid Id = new("c35793df-095e-4bc9-8b04-a027a1a8cc91");

    public async ValueTask ImportAsync(Skin source, SkinBindingSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        SkeletonBlock skeleton = await context.GetBlockAsync<SkeletonBlock>(output.Target, cancellationToken).ConfigureAwait(false);
        var matrices = new Matrix4x4[source.JointsCount];
        JsonNode description = JsonNode.Parse(source.LogicalParent.GetJsonPreview())!["skins"]![source.LogicalIndex]!;
        VertexStream? sourceMatrices = description["inverseBindMatrices"] is JsonNode index
            ? new GltfAccessorReader(source.LogicalParent).ReadComponents(source.LogicalParent.LogicalAccessors[index.GetValue<int>()], cancellationToken)
            : null;
        if (sourceMatrices is not null && (sourceMatrices.ComponentCount != 16 || sourceMatrices.Count < matrices.Length))
            throw new InvalidDataException("Inverse bind matrices must contain a MAT4 for each source joint.");
        for (int joint = 0; joint < matrices.Length; joint++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceMatrices is null) matrices[joint] = Matrix4x4.Identity;
            else
            {
                ReadOnlySpan<double> values = sourceMatrices.Values.Span.Slice(joint * 16, 16);
                matrices[joint] = new((float)values[0], (float)values[1], (float)values[2], (float)values[3],
                    (float)values[4], (float)values[5], (float)values[6], (float)values[7],
                    (float)values[8], (float)values[9], (float)values[10], (float)values[11],
                    (float)values[12], (float)values[13], (float)values[14], (float)values[15]);
            }
        }
        var binding = new SkinBindingBlock(matrices, skeleton.JointBones);
        try { output.SetBlock(binding); }
        catch { binding.Dispose(); throw; }
    }
}
