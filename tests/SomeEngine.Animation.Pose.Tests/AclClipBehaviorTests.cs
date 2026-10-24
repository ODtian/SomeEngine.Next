using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class AclClipBehaviorTests
{
    [Fact]
    public void CompressedScalarAndSubsetBatchesStayWithinTheSourcePoseTolerance()
    {
        const float precision = .001f;
        var skeleton = new Skeleton([-1, 0, 1], [At(0, 0, 0), At(0, 1, 0), At(.5f, 0, 0)], [10, 20, 30]);
        var raw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 0), new(2, 0, 0, 0)]),
            new(0, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
            new(1, BoneTransformProperty.Scale, ClipInterpolation.Linear, [0, 1],
                [new(1, 1, 1, 0), new(1.2f, 1, .8f, 0)]),
            new(2, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(.5f, 0, 0, 0), new(.75f, 0, 0, 0)]),
        ]);
        var source = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw));
        AclClipData data = AclClipCompressor.Compress(source, sampleRate: 60, precision, shellDistance: 1);
        var compressed = new AnimationClip(skeleton, data.Duration, ClipSampler.Create<AclClipData, AclClipSampler>(data));
        BoneTransform[] referenceLocal = new BoneTransform[skeleton.BoneCount];
        BoneTransform[] decodedLocal = new BoneTransform[skeleton.BoneCount];
        var poses = new PoseStorage();
        using SkeletonInstance reference = poses.CreateInstance(skeleton);
        using SkeletonInstance decoded = poses.CreateInstance(skeleton);
        foreach (float time in new float[] { 0, .125f, .371f, .8f, 1 })
        {
            source.SampleLocal(time, referenceLocal);
            compressed.SampleLocal(time, decodedLocal);
            reference.Update(referenceLocal, time);
            decoded.Update(decodedLocal, time);
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
                Within(reference.ModelPose.Transforms[bone], decoded.ModelPose.Transforms[bone], precision);
        }

        ClipSampleSelection[] selections = [new(compressed, [2, 0]), new(compressed, [1]), new(compressed)];
        var batch = new ClipSampleBatch(selections);
        float[] times = [.125f, .371f, .8f];
        BoneTransform[] local = new BoneTransform[batch.OutputCount];
        Matrix4x4[] model = new Matrix4x4[batch.OutputCount];
        batch.SampleLocal(times, local);
        batch.SampleModel(times, model);
        for (int sample = 0; sample < times.Length; sample++)
        {
            source.SampleLocal(times[sample], referenceLocal);
            reference.Update(referenceLocal, times[sample]);
            PoseRange output = batch.GetOutputRange(sample);
            for (int selected = 0; selected < selections[sample].Bones.Length; selected++)
            {
                int bone = selections[sample].Bones[selected];
                Within(referenceLocal[bone].ToMatrix(), local[output.Start + selected].ToMatrix(), precision);
                Within(reference.ModelPose.Transforms[bone], model[output.Start + selected], precision);
            }
        }
    }

    [Fact]
    public async Task ReopenedCompressedClipKeepsSamplingAndGraphPlaybackAfterTheAssetCloses()
    {
        using var files = new ClipFiles();
        var project = new AssetProject(files.Directory, []);
        var importer = new GltfSourceImporter(PoseTestAssets.AddAssetCapabilities(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
            PoseTestAssets.BuildVersion, PoseSceneImporter.Id);
        await project.ImportAsync(files.Source, importer);
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
            AclClipBlock block = await asset.GetBlockAsync<AclClipBlock>();
            Assert.Equal(skeletonEntry.AssetGuid, block.Skeleton.Value);
            clip = block.CreateClip(skeleton);
        }

        Assert.Equal(1, clip.Duration);
        Assert.Equal(0, clip.RootMotionBone);
        Assert.Equal(new ClipEvent[] { new(.25f, 7), new(.75f, 9) }, clip.Events.ToArray());
        var local = new BoneTransform[skeleton.BoneCount];
        clip.SampleLocal(.25f, local);
        Within(Matrix4x4.CreateTranslation(.5f, 0, 0), local[0].ToMatrix(), .001f);
        Within(Matrix4x4.CreateTranslation(0, 1, 0), local[1].ToMatrix(), .001f);
        var roots = new ClipSampleBatch([new(clip, [0]), new(clip, [0])]);
        var rootSamples = new BoneTransform[roots.OutputCount];
        roots.SampleLocal([.25f, .75f], rootSamples);
        Within(Matrix4x4.CreateTranslation(.5f, 0, 0), rootSamples[0].ToMatrix(), .001f);
        Within(Matrix4x4.CreateTranslation(1.5f, 0, 0), rootSamples[1].ToMatrix(), .001f);

        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance);
        using var builder = new AnimationBuilder(world);
        builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new PoseResultId(0)));
        Entity animation = builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);
        var context = new ImmediateSystemContext(world, 0, 0, .5);
        systems.Update(ref context);
        Within(Matrix4x4.CreateTranslation(1, 1, 0), instance.ModelPose.Transforms[1], .001f);
        systems.Update(ref context);
        Within(Matrix4x4.CreateTranslation(2, 1, 0), instance.ModelPose.Transforms[1], .001f);
        Assert.Equal(1u, world.Read<Timeline>(animation).Completions);
        Assert.Equal(1, instance.ModelPose.SampleTime);
    }

    private static void Within(Matrix4x4 expected, Matrix4x4 actual, float precision)
    {
        foreach (Vector3 point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.InRange(Vector3.Distance(Vector3.Transform(point, expected), Vector3.Transform(point, actual)), 0, precision);
    }

    private static BoneTransform At(float x, float y, float z) => new()
    {
        Translation = new(x, y, z), Rotation = Quaternion.Identity, Scale = Vector3.One,
    };

    private sealed class ClipFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "acl-clip-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "clip.gltf");

        public ClipFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            float[] values = [0, 1, 0, 0, 0, 2, 0, 0];
            var bytes = new byte[values.Length * sizeof(float)];
            for (int index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
            var document = JsonNode.Parse("""
                {"asset":{"version":"2.0"},
                 "nodes":[{"name":"tip","translation":[0,1,0]},{"name":"root","children":[0]}],
                 "skins":[{"name":"Rig","joints":[0,1],"skeleton":1,"extras":{"someengine_id":"rig"}}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":8},
                    {"buffer":0,"byteOffset":8,"byteLength":24}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[1]},
                    {"bufferView":1,"componentType":5126,"count":2,"type":"VEC3"}],
                 "animations":[{"name":"Move","samplers":[{"input":0,"output":1,"interpolation":"LINEAR"}],
                    "channels":[{"sampler":0,"target":{"node":1,"path":"translation"}}],
                    "extras":{"someengine_id":"move","someengine_events":[{"time":0.25,"id":7},{"time":0.75,"id":9}]}}],
                 "scenes":[{"nodes":[1],"extras":{"someengine_id":"scene"}}],"scene":0}
                """)!.AsObject();
            document["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes),
            });
            File.WriteAllText(Source, document.ToJsonString());
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
            metadata.Importers.Add(new(PoseSceneImporter.Id, default));
            metadata.Importers.Add(new(GltfSkeletonImporter.Id, default));
            metadata.Importers.Add(new(AclClipImporter.Id, JsonSerializer.SerializeToElement(
                new AclClipSettings(RootMotionNode: 1, SampleRate: 60, Precision: .001f, ShellDistance: 1),
                AclClipSettingsJson.Default.AclClipSettings)));
            SourceMeta.Save(Source, metadata);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
