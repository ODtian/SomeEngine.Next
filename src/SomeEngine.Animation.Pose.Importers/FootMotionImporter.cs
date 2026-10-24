using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record FootMotionSettings(float SampleRate = 60, int SkinIndex = 0, int RootMotionNode = 0,
    float GroundHeight = 0, float HeightTolerance = 0.03f, float SpeedTolerance = 0.1f, int UpAxis = 1);

[JsonSerializable(typeof(FootMotionSettings))]
public sealed partial class FootMotionSettingsJson : JsonSerializerContext;

public sealed class FootMotionSettingsProvider : IAssetImporterSettings<FootMotionSettings>
{
    public FootMotionSettings DefaultSettings => new();
    public JsonTypeInfo<FootMotionSettings> TypeInfo => FootMotionSettingsJson.Default.FootMotionSettings;
}

/// <summary>Produces clip foot data alongside the caller-selected pose encoding.</summary>
[AssetImporter("7a4a8986-b8cf-4fa1-aa4a-c0240e47c5ae", typeof(FootMotionBlock),
    SettingsProvider = typeof(FootMotionSettingsProvider), VersionInputs = new[] { typeof(ModelRoot), typeof(FootMotion) })]
public sealed class FootMotionImporter : IAssetImporter<GltfAnimation, FootMotionSettings>
{
    public static readonly Guid Id = new("7a4a8986-b8cf-4fa1-aa4a-c0240e47c5ae");

    public async ValueTask ImportAsync(GltfAnimation source, FootMotionSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Skin skin = source.LogicalParent.LogicalSkins[settings.SkinIndex];
        SkeletonAsset asset = await context.GetAssetAsync<Skin, SkeletonAsset>(skin, cancellationToken).ConfigureAwait(false);
        SkeletonBlock block = await context.GetBlockAsync<SkeletonBlock>(asset, cancellationToken).ConfigureAwait(false);
        Skeleton skeleton = block.CreateSkeleton();
        RawClipData data = GltfClipReader.Read(source, skin, skeleton, settings.RootMotionNode, cancellationToken);
        var clip = new AnimationClip(skeleton, data.Duration, ClipSampler.Create<RawClipData, RawClipSampler>(data), data.Events, data.RootMotionBone);
        Dictionary<Node, short> bones = GltfClipReader.GetBoneIndices(skin, skeleton);
        FootBones[] feet = skin.Extras!["someengine_feet"]!.AsArray().Select(foot => new FootBones(
            bones[source.LogicalParent.LogicalNodes[foot!["heel"]!.GetValue<int>()]],
            bones[source.LogicalParent.LogicalNodes[foot["toe"]!.GetValue<int>()]])).ToArray();
        FootContactMarker[] markers = source.Extras?["someengine_foot_contacts"] is JsonArray contacts
            ? contacts.Select(contact => new FootContactMarker(contact!["foot"]!.GetValue<int>(),
                contact["start"]!.GetValue<float>(), contact["end"]!.GetValue<float>(),
                Enum.Parse<FootContactKind>(contact["kind"]!.GetValue<string>()))).ToArray() : [];
        var analysis = new FootAnalysisSettings(settings.SampleRate, settings.GroundHeight,
            settings.HeightTolerance, settings.SpeedTolerance, settings.UpAxis);
        FootMotion motion = FootMotion.Analyze(clip, data.RootMotionBone, feet, in analysis, markers);
        var result = new FootMotionBlock(asset.Reference, motion);
        try { output.SetBlock(result); }
        catch { result.Dispose(); throw; }
    }
}
