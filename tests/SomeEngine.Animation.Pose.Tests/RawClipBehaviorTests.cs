using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class RawClipBehaviorTests
{
    [Fact]
    public async Task ReopenedClipSamplesTheSourceInterpolationAndKeepsItsMotionAndEvents()
    {
        using var files = new ClipFiles();
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry skeletonEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        AssetEntry clipEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == AnimationClipAsset.FileType);
        Skeleton skeleton;
        using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, skeletonEntry.Path)))
            skeleton = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();
        string saved = Path.Combine(files.Directory, "saved.animation-clip.asset");
        using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(Path.Combine(files.Directory, clipEntry.Path)))
            await asset.SaveAsync(saved);
        AnimationClip clip;
        using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(saved))
        {
            RawClipBlock block = await asset.GetBlockAsync<RawClipBlock>();
            Assert.Equal(skeletonEntry.AssetGuid, block.Skeleton.Value);
            clip = block.CreateClip(skeleton);
        }

        Assert.Equal(2, clip.Duration);
        Assert.Equal(0, clip.RootMotionBone);
        Assert.Equal(new ClipEvent[] { new(0.5f, 7), new(1.5f, 9) }, clip.Events.ToArray());
        BoneTransform[] pose = new BoneTransform[skeleton.BoneCount];
        float[] times = [0.5f, 1, 1.5f];
        float[] tipHeights = [1.96875f, 2.75f, 3.15625f];
        float[] scales = [1, 2, 2];
        Vector3[] directions = [new(0.70710677f, 0.70710677f, 0), Vector3.UnitY, new(-0.70710677f, 0.70710677f, 0)];
        for (int sample = 0; sample < times.Length; sample++)
        {
            clip.SampleLocal(times[sample], pose);
            Assert.Equal(new Vector3(times[sample], 0, 0), pose[0].Translation);
            Assert.Equal(new Vector3(scales[sample]), pose[0].Scale);
            Assert.True(Vector3.Distance(directions[sample], Vector3.Transform(Vector3.UnitX, pose[0].Rotation)) < 0.00001f);
            Assert.Equal(new Vector3(0, tipHeights[sample], 0), pose[1].Translation);
            Assert.Equal(1, pose[1].Rotation.Length(), 5);
            Assert.Equal(Vector3.One, pose[1].Scale);
        }
        clip.SampleLocal(1, pose);
        Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, pose[1].Rotation)) < 0.00001f);
    }

    [Fact]
    public async Task SourceMetadataSelectsAConsumerClipCodecThroughTheSameSamplingEntry()
    {
        using var files = new ClipFiles();
        JsonNode animation = files.Document["animations"]![0]!;
        JsonArray channels = animation["channels"]!.AsArray();
        JsonArray samplers = animation["samplers"]!.AsArray();
        while (channels.Count > 1) channels.RemoveAt(channels.Count - 1);
        while (samplers.Count > 1) samplers.RemoveAt(samplers.Count - 1);
        files.WriteSource();
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.RemoveAll(value => value.ImporterId == RawClipImporter.Id);
        metadata.Importers.Add(new(ConsumerLinearClipImporter.Id, JsonSerializer.SerializeToElement(
            new RawClipSettings(RootMotionNode: 1), RawClipSettingsJson.Default.RawClipSettings)));
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry skeletonEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        AssetEntry clipEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == AnimationClipAsset.FileType);
        Skeleton skeleton;
        using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, skeletonEntry.Path)))
            skeleton = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();
        AnimationClip clip;
        using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(Path.Combine(files.Directory, clipEntry.Path)))
        {
            AnimationClipBlock block = await asset.GetBlockAsync<ConsumerLinearClipBlock>();
            clip = block.CreateClip(skeleton);
        }

        BoneTransform[] pose = new BoneTransform[skeleton.BoneCount];
        foreach (float time in new float[] { 0.5f, 1.5f })
        {
            clip.SampleLocal(time, pose);
            Assert.Equal(new Vector3(time, 0, 0), pose[0].Translation);
            Assert.Equal(Vector3.UnitY, pose[1].Translation);
        }
        Assert.Equal(2, clip.Duration);
        Assert.Equal(0, clip.RootMotionBone);
        Assert.Equal(new ClipEvent[] { new(0.5f, 7), new(1.5f, 9) }, clip.Events.ToArray());
    }

    [Fact]
    public async Task UniformQuantizedClipKeepsTrsSamplesAndInterpolatesAdjacentTimesAfterReopening()
    {
        using var files = new ClipFiles();
        JsonNode animation = files.Document["animations"]![0]!;
        JsonArray channels = animation["channels"]!.AsArray();
        JsonArray samplers = animation["samplers"]!.AsArray();
        while (channels.Count > 3) channels.RemoveAt(channels.Count - 1);
        while (samplers.Count > 3) samplers.RemoveAt(samplers.Count - 1);
        samplers[2]!["interpolation"] = "LINEAR";
        files.WriteSource();
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.RemoveAll(value => value.ImporterId == RawClipImporter.Id);
        metadata.Importers.Add(new(QuantizedClipImporter.Id, JsonSerializer.SerializeToElement(
            new QuantizedClipSettings(SampleRate: 4, RootMotionNode: 1), QuantizedClipSettingsJson.Default.QuantizedClipSettings)));
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry skeletonEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        AssetEntry clipEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == AnimationClipAsset.FileType);
        Skeleton skeleton;
        using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, skeletonEntry.Path)))
            skeleton = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();
        AnimationClip clip;
        using (AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(Path.Combine(files.Directory, clipEntry.Path)))
        {
            QuantizedClipBlock block = await asset.GetBlockAsync<QuantizedClipBlock>();
            Assert.Equal(9, block.Data.SampleCount);
            Assert.Equal(skeletonEntry.AssetGuid, block.Skeleton.Value);
            clip = block.CreateClip(skeleton);
        }
        var pose = new BoneTransform[skeleton.BoneCount];
        foreach (float time in new float[] { .375f, 1.375f })
        {
            clip.SampleLocal(time, pose);
            Assert.InRange(Vector3.Distance(new(time, 0, 0), pose[0].Translation), 0, .0001f);
            float angle = MathF.PI * .5f * time;
            Assert.InRange(Vector3.Distance(new(MathF.Cos(angle), MathF.Sin(angle), 0),
                Vector3.Transform(Vector3.UnitX, pose[0].Rotation)), 0, .0002f);
            float scale = time < 1 ? 1 + time : 2 * time;
            Assert.InRange(Vector3.Distance(new(scale), pose[0].Scale), 0, .0001f);
            Assert.InRange(Vector3.Distance(Vector3.UnitY, pose[1].Translation), 0, .0001f);
        }
        Assert.Equal(2, clip.Duration);
        Assert.Equal(0, clip.RootMotionBone);
        Assert.Equal(new ClipEvent[] { new(.5f, 7), new(1.5f, 9) }, clip.Events.ToArray());
    }

    private static GltfSourceImporter SourceImporter()
        => new(services => PoseTestAssets.AddAssetCapabilities(services), PoseTestAssets.BuildVersion, PoseSceneImporter.Id);

    private sealed class ClipFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pose-clip-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "clip.gltf");
        public JsonObject Document { get; }

        public ClipFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var bytes = new List<byte>();
            var accessors = new JsonArray();
            var views = new JsonArray();
            int AddAccessor(float[] values, string type, int components)
            {
                int offset = bytes.Count;
                foreach (float value in values)
                {
                    byte[] encoded = new byte[sizeof(float)];
                    BinaryPrimitives.WriteSingleLittleEndian(encoded, value);
                    bytes.AddRange(encoded);
                }
                var accessor = new JsonObject
                {
                    ["bufferView"] = views.Count, ["componentType"] = 5126,
                    ["count"] = values.Length / components, ["type"] = type,
                };
                if (type == "SCALAR")
                {
                    accessor["min"] = new JsonArray(values[0]);
                    accessor["max"] = new JsonArray(values[^1]);
                }
                views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * sizeof(float) });
                accessors.Add(accessor);
                return accessors.Count - 1;
            }

            int times = AddAccessor([0, 2], "SCALAR", 1);
            int translation = AddAccessor([0, 0, 0, 2, 0, 0], "VEC3", 3);
            int rotation = AddAccessor([0, 0, 0, 1, 0, 0, 1, 0], "VEC4", 4);
            int scaleTimes = AddAccessor([0, 1, 2], "SCALAR", 1);
            int scale = AddAccessor([1, 1, 1, 2, 2, 2, 4, 4, 4], "VEC3", 3);
            int tipTranslation = AddAccessor([0, 0, 0, 0, 1, 0, 0, 2, 0, 0, -1, 0, 0, 3, 0, 0, 0, 0], "VEC3", 3);
            int tipRotation = AddAccessor([0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0], "VEC4", 4);
            Document = JsonNode.Parse($$$$"""
                {"asset":{"version":"2.0"},
                 "nodes":[{"name":"tip","translation":[0,1,0]},{"name":"root","children":[0]}],
                 "skins":[{"name":"Rig","joints":[0,1],"skeleton":1,"extras":{"someengine_id":"rig"}}],
                 "animations":[{"name":"Move","samplers":[
                    {"input":{{{{times}}}},"output":{{{{translation}}}},"interpolation":"LINEAR"},
                    {"input":{{{{times}}}},"output":{{{{rotation}}}},"interpolation":"LINEAR"},
                    {"input":{{{{scaleTimes}}}},"output":{{{{scale}}}},"interpolation":"STEP"},
                    {"input":{{{{times}}}},"output":{{{{tipTranslation}}}},"interpolation":"CUBICSPLINE"},
                    {"input":{{{{times}}}},"output":{{{{tipRotation}}}},"interpolation":"CUBICSPLINE"}],
                    "channels":[{"sampler":0,"target":{"node":1,"path":"translation"}},
                       {"sampler":1,"target":{"node":1,"path":"rotation"}},
                       {"sampler":2,"target":{"node":1,"path":"scale"}},
                       {"sampler":3,"target":{"node":0,"path":"translation"}},
                       {"sampler":4,"target":{"node":0,"path":"rotation"}}],
                    "extras":{"someengine_id":"move","someengine_events":[{"time":0.5,"id":7},{"time":1.5,"id":9}]}}],
                 "scenes":[{"nodes":[1],"extras":{"someengine_id":"scene"}}],"scene":0}
                """)!.AsObject();
            Document["bufferViews"] = views;
            Document["accessors"] = accessors;
            Document["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Count,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()),
            });
            WriteSource();
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
            metadata.Importers.Add(new(PoseSceneImporter.Id, default));
            metadata.Importers.Add(new(GltfSkeletonImporter.Id, default));
            metadata.Importers.Add(new(RawClipImporter.Id, JsonSerializer.SerializeToElement(
                new RawClipSettings(RootMotionNode: 1), RawClipSettingsJson.Default.RawClipSettings)));
            SourceMeta.Save(Source, metadata);
        }

        public void WriteSource() => File.WriteAllText(Source, Document.ToJsonString());
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

[AssetBlock("SomeEngine.Animation.Pose.Tests.ConsumerLinearClip"), BinarySerializable,
 BinaryFields(nameof(SkeletonReference), nameof(DurationSeconds), nameof(RootBone), nameof(Markers), nameof(EndX))]
public sealed partial class ConsumerLinearClipBlock : AnimationClipBlock
{
    public partial AssetId<SkeletonAsset> SkeletonReference { get; set; }
    public partial float DurationSeconds { get; set; }
    public partial short RootBone { get; set; }
    public partial IList<ClipEvent> Markers { get; set; }
    public partial float EndX { get; set; }
    [BinaryIgnore] public override AssetId<SkeletonAsset> Skeleton => SkeletonReference;
    [BinaryIgnore] public override float Duration => DurationSeconds;
    [BinaryIgnore] public override short RootMotionBone => RootBone;
    [BinaryIgnore] public override ImmutableArray<ClipEvent> Events => Markers.ToImmutableArray();
    [BinaryIgnore] public override ClipSampler Sampler => ClipSampler.Create<ConsumerLinearClipData, ConsumerLinearClipSampler>(
        new(RootBone, EndX / DurationSeconds));
}

public sealed record ConsumerLinearClipData(short Bone, float Velocity);

public readonly struct ConsumerLinearClipSampler : IClipSampler<ConsumerLinearClipData>
{
    public static void SampleLocal(ConsumerLinearClipData data, float time, Span<BoneTransform> destination)
        => destination[data.Bone].Translation.X = data.Velocity * time;
}

[AssetImporter("ea226531-6af5-4d46-8ff2-e10b3397814c", typeof(ConsumerLinearClipBlock), SettingsProvider = typeof(RawClipSettingsProvider))]
public sealed class ConsumerLinearClipImporter : IAssetImporter<GltfAnimation, RawClipSettings>
{
    public static readonly Guid Id = new("ea226531-6af5-4d46-8ff2-e10b3397814c");
    public async ValueTask ImportAsync(GltfAnimation source, RawClipSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        SharpGLTF.Schema2.Skin skin = source.LogicalParent.LogicalSkins[settings.SkinIndex];
        SkeletonAsset asset = await context.GetAssetAsync<SharpGLTF.Schema2.Skin, SkeletonAsset>(skin, cancellationToken);
        SkeletonBlock skeleton = await context.GetBlockAsync<SkeletonBlock>(asset, cancellationToken);
        RawClipData data = GltfClipReader.Read(source, skin, skeleton.CreateSkeleton(), settings.RootMotionNode, cancellationToken);
        Vector4 end = data.Channels[0].Values[^1];
        output.SetBlock(new ConsumerLinearClipBlock
        {
            SkeletonReference = asset.Reference,
            DurationSeconds = data.Duration,
            RootBone = data.RootMotionBone,
            Markers = data.Events.ToArray(),
            EndX = end.X,
        });
    }
}
