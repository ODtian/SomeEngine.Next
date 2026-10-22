using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record RawClipSettings(int SkinIndex = 0, int RootMotionNode = -1);

[JsonSerializable(typeof(RawClipSettings))]
public sealed partial class RawClipSettingsJson : JsonSerializerContext;

public sealed class RawClipSettingsProvider : IAssetImporterSettings<RawClipSettings>
{
    public RawClipSettings DefaultSettings => new();
    public JsonTypeInfo<RawClipSettings> TypeInfo => RawClipSettingsJson.Default.RawClipSettings;
}

[AssetImporter("8261eabb-c43e-408a-8354-ced229a2ce84", typeof(RawClipBlock),
    SettingsProvider = typeof(RawClipSettingsProvider), VersionInputs = new[] { typeof(ModelRoot) })]
public sealed class RawClipImporter : IAssetImporter<GltfAnimation, RawClipSettings>
{
    public static readonly Guid Id = new("8261eabb-c43e-408a-8354-ced229a2ce84");

    public async ValueTask ImportAsync(GltfAnimation source, RawClipSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        Skin skin = source.LogicalParent.LogicalSkins[settings.SkinIndex];
        SkeletonAsset asset = await context.GetAssetAsync<Skin, SkeletonAsset>(skin, cancellationToken).ConfigureAwait(false);
        SkeletonBlock skeleton = await context.GetBlockAsync<SkeletonBlock>(asset, cancellationToken).ConfigureAwait(false);
        RawClipData data = GltfClipReader.Read(source, skin, skeleton.CreateSkeleton(), settings.RootMotionNode, cancellationToken);
        var clip = new RawClipBlock(asset.Reference, data);
        try { output.SetBlock(clip); }
        catch { clip.Dispose(); throw; }
    }
}
