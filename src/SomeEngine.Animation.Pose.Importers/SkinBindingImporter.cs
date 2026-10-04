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
        IReadOnlyList<Matrix4x4> sourceMatrices = source.InverseBindMatrices;
        for (int joint = 0; joint < matrices.Length; joint++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            matrices[joint] = sourceMatrices.Count == 0 ? Matrix4x4.Identity : sourceMatrices[joint];
        }
        var binding = new SkinBindingBlock(matrices, skeleton.JointBones);
        try { output.SetBlock(binding); }
        catch { binding.Dispose(); throw; }
    }
}
