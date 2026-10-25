using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Checks.PoseExample;

[AssetCapabilities]
public static partial class PoseExampleCapabilities;

/// <summary>Example source format: one translation velocity and Z rotation rate per own bone.</summary>
public sealed record ConsumerPoseSource(Guid Skeleton, float Duration, float[] Velocities, float[] Turns);
public sealed record ConsumerPoseSettings(float Speed = 1);

public sealed class ConsumerPoseSettingsCodec : IAssetImporterSettings<ConsumerPoseSettings>
{
    public ConsumerPoseSettings DefaultSettings => new();
    public JsonTypeInfo<ConsumerPoseSettings> TypeInfo => PoseExampleJson.Default.ConsumerPoseSettings;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConsumerPoseSource))]
[JsonSerializable(typeof(ConsumerPoseSettings))]
internal partial class PoseExampleJson : JsonSerializerContext;

[AssetBlock("SomeEngine.Animation.Checks.ConsumerPose"), BinarySerializable,
 BinaryFields(nameof(SkeletonReference), nameof(DurationSeconds), nameof(Velocities), nameof(Turns))]
public sealed partial class ConsumerPoseBlock : AnimationClipBlock
{
    public partial AssetId<SkeletonAsset> SkeletonReference { get; set; }
    public partial float DurationSeconds { get; set; }
    public partial IList<float> Velocities { get; set; }
    public partial IList<float> Turns { get; set; }
    [BinaryIgnore] public override AssetId<SkeletonAsset> Skeleton => SkeletonReference;
    [BinaryIgnore] public override float Duration => DurationSeconds;
    [BinaryIgnore] public override short RootMotionBone => -1;
    [BinaryIgnore] public override ImmutableArray<ClipEvent> Events => [];
    [BinaryIgnore] public override ClipSampler Sampler =>
        ClipSampler.Create<ConsumerPoseData, ConsumerPoseSampler>(new(Velocities.ToArray(), Turns.ToArray()));
}

public sealed record ConsumerPoseData(float[] Velocities, float[] Turns);

public readonly struct ConsumerPoseSampler : IClipSampler<ConsumerPoseData>
{
    public static void SampleLocal(ConsumerPoseData data, float time, Span<BoneTransform> destination)
    {
        for (int bone = 0; bone < data.Turns.Length; bone++)
        {
            int offset = bone * 3;
            destination[bone].Translation +=
                new Vector3(data.Velocities[offset], data.Velocities[offset + 1], data.Velocities[offset + 2]) * time;
            destination[bone].Rotation = Quaternion.Concatenate(destination[bone].Rotation,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, data.Turns[bone] * time));
        }
    }
}

[AssetImporter("cd852431-d49b-4a15-a46d-58533751e665", typeof(ConsumerPoseBlock),
    SettingsProvider = typeof(ConsumerPoseSettingsCodec))]
public sealed class ConsumerPoseImporter : IAssetImporter<ConsumerPoseSource, ConsumerPoseSettings>
{
    public static readonly Guid Id = new("cd852431-d49b-4a15-a46d-58533751e665");
    public ValueTask ImportAsync(ConsumerPoseSource source, ConsumerPoseSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new ConsumerPoseBlock
        {
            SkeletonReference = new(source.Skeleton), DurationSeconds = source.Duration,
            Velocities = source.Velocities.Select(value => value * settings.Speed).ToArray(),
            Turns = source.Turns.Select(value => value * settings.Speed).ToArray(),
        });
        return ValueTask.CompletedTask;
    }
}
