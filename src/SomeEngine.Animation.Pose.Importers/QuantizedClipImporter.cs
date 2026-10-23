using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record QuantizedClipSettings(float SampleRate = 30, int SkinIndex = 0, int RootMotionNode = -1);

[JsonSerializable(typeof(QuantizedClipSettings))]
public sealed partial class QuantizedClipSettingsJson : JsonSerializerContext;

public sealed class QuantizedClipSettingsProvider : IAssetImporterSettings<QuantizedClipSettings>
{
    public QuantizedClipSettings DefaultSettings => new();
    public JsonTypeInfo<QuantizedClipSettings> TypeInfo => QuantizedClipSettingsJson.Default.QuantizedClipSettings;
}

[AssetImporter("2e8c93e1-6220-4e63-a50a-c1d4271f0121", typeof(QuantizedClipBlock),
    SettingsProvider = typeof(QuantizedClipSettingsProvider), VersionInputs = new[] { typeof(ModelRoot), typeof(QuantizedClipData) })]
public sealed class QuantizedClipImporter : IAssetImporter<GltfAnimation, QuantizedClipSettings>
{
    public static readonly Guid Id = new("2e8c93e1-6220-4e63-a50a-c1d4271f0121");

    public async ValueTask ImportAsync(GltfAnimation source, QuantizedClipSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Skin skin = source.LogicalParent.LogicalSkins[settings.SkinIndex];
        SkeletonAsset asset = await context.GetAssetAsync<Skin, SkeletonAsset>(skin, cancellationToken).ConfigureAwait(false);
        SkeletonBlock block = await context.GetBlockAsync<SkeletonBlock>(asset, cancellationToken).ConfigureAwait(false);
        Skeleton skeleton = block.CreateSkeleton();
        RawClipData curves = GltfClipReader.Read(source, skin, skeleton, settings.RootMotionNode, cancellationToken);
        var raw = new AnimationClip(skeleton, curves.Duration, ClipSampler.Create<RawClipData, RawClipSampler>(curves),
            curves.Events, curves.RootMotionBone);
        var clip = new QuantizedClipBlock(asset.Reference, QuantizedClipData.Encode(raw, settings.SampleRate));
        try { output.SetBlock(clip); }
        catch { clip.Dispose(); throw; }
    }
}
