using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record AclClipSettings(int SkinIndex = 0, int RootMotionNode = -1,
    float SampleRate = 60, float Precision = .001f, float ShellDistance = 1);

[JsonSerializable(typeof(AclClipSettings))]
public sealed partial class AclClipSettingsJson : JsonSerializerContext;

public sealed class AclClipSettingsProvider : IAssetImporterSettings<AclClipSettings>
{
    public AclClipSettings DefaultSettings => new();
    public JsonTypeInfo<AclClipSettings> TypeInfo => AclClipSettingsJson.Default.AclClipSettings;
}

[AssetImporter("3d681491-3115-4852-b797-4770b7d4ca26", typeof(AclClipBlock),
    SettingsProvider = typeof(AclClipSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class AclClipImporter : IAssetImporter<GltfAnimation, AclClipSettings>
{
    public static readonly Guid Id = new("3d681491-3115-4852-b797-4770b7d4ca26");

    public async ValueTask ImportAsync(GltfAnimation source, AclClipSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Skin skin = source.LogicalParent.LogicalSkins[settings.SkinIndex];
        SkeletonAsset asset = await context.GetAssetAsync<Skin, SkeletonAsset>(skin, cancellationToken).ConfigureAwait(false);
        SkeletonBlock block = await context.GetBlockAsync<SkeletonBlock>(asset, cancellationToken).ConfigureAwait(false);
        Skeleton skeleton = block.CreateSkeleton();
        RawClipData raw = GltfClipReader.Read(source, skin, skeleton, settings.RootMotionNode, cancellationToken);
        var reference = new AnimationClip(skeleton, raw.Duration,
            ClipSampler.Create<RawClipData, RawClipSampler>(raw), raw.Events, raw.RootMotionBone);
        AclClipData data = AclClipCompressor.Compress(reference, settings.SampleRate, settings.Precision, settings.ShellDistance);
        var clip = new AclClipBlock(asset.Reference, data);
        try { output.SetBlock(clip); }
        catch { clip.Dispose(); throw; }
    }
}
