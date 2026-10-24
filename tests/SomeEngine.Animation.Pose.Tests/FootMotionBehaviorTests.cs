using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class FootMotionBehaviorTests
{
    [Fact]
    public async Task ImportedFootTrajectoriesKeepAutomaticContactsAndActionMarkerCorrectionsAfterReopening()
    {
        // Attack, turn and landing are example actions for the general contact model.
        using var files = new ActionFiles();
        Dictionary<string, ImportedAction> actions = await files.ImportAsync();
        FootMotion attack = actions["Attack"].Motion;
        FootMotion turn = actions["Turn"].Motion;
        FootMotion landing = actions["Landing"].Motion;
        Assert.Equal(new FootContactSegment[]
        {
            new(0, 0.25f, FootContactKind.Support), new(0.25f, 1, FootContactKind.Free),
        }, attack.Contacts(0).ToArray());
        Assert.Equal(new FootContactSegment[]
        {
            new(0, 0.25f, FootContactKind.Free), new(0.25f, 0.75f, FootContactKind.ToePivot),
            new(0.75f, 1, FootContactKind.Sliding),
        }, turn.Contacts(0).ToArray());
        Assert.Equal(new FootContactSegment[]
        {
            new(0, 0.5f, FootContactKind.Free), new(0.5f, 0.75f, FootContactKind.Support),
            new(0.75f, 1, FootContactKind.Sliding),
        }, landing.Contacts(0).ToArray());

        FootTrajectory lifted = attack.Sample(0, 0.625f);
        Near(new(0.625f, 0, 0), lifted.Root.Translation);
        Near(new(0.55f, 1, 0), lifted.Heel);
        Near(new(0.95f, 1, 0), lifted.Toe);
        FootTrajectory pivot = turn.Sample(0, 0.5f);
        Near(new(0.5f, 0, 0), pivot.Root.Translation);
        Near(new(0.70710677f, 0, -0.70710677f), Vector3.TransformNormal(Vector3.UnitX, pivot.Root));
        Near(new(0.35857865f, 0, 0.14142136f), pivot.Heel);
        Near(new(0.6414214f, 0, -0.14142136f), pivot.Toe);
        FootTrajectory landed = landing.Sample(0, 0.875f);
        Near(new(0.875f, 0, 0), landed.Root.Translation);
        Near(new(0.8f, 0, 0), landed.Heel);
        Near(new(1.2f, 0, 0), landed.Toe);
    }

    [Fact]
    public async Task SelectedSequenceAndBlendUseTheSameTimeOriginForTheNextContactAndFootTrajectory()
    {
        // These named actions are examples; the selected path and its curves determine each query.
        using var files = new ActionFiles();
        Dictionary<string, ImportedAction> actions = await files.ImportAsync();
        PosePath attack = PosePath.FromClip(actions["Attack"].Clip, actions["Attack"].Motion);
        PosePath turn = PosePath.FromClip(actions["Turn"].Clip, actions["Turn"].Motion);
        PosePath landing = PosePath.FromClip(actions["Landing"].Clip, actions["Landing"].Motion);
        PosePath selected = PosePath.Sequence([attack, turn, landing]);
        var request = new PosePredictionRequest(selected, new PoseTimeOrigin(10, 0.4f, 2), Conditions: 81);
        var prediction = new FootPrediction(request, lookAhead: 1.5f);
        Assert.True(prediction.TryGetNextContact(0, afterTime: 10, calculationTime: 10.01, out FootPredictionSample next));
        Assert.Equal(request, next.Prediction.Request);
        Assert.Equal(10.425, next.Prediction.Time, 6);
        Assert.Equal(1.25f, next.Prediction.PathTime);
        Assert.Equal(new(1.25f, 1.75f, FootContactKind.ToePivot), next.Contact);
        Near(new(0.25f, 0, 0), next.Trajectory.Root.Translation);
        Near(new(0.0652241f, 0, 0.07653669f), next.Trajectory.Heel);
        Near(new(0.4347759f, 0, -0.07653669f), next.Trajectory.Toe);
        Assert.True(prediction.TryGetNextContact(0, afterTime: 10.5, calculationTime: 10.1, out FootPredictionSample land));
        Assert.Equal(11.05, land.Prediction.Time, 6);
        Assert.Equal(new(2.5f, 2.75f, FootContactKind.Support), land.Contact);
        Near(new(0.5f, 0, 0), land.Trajectory.Root.Translation);
        Near(new(0.8f, 0, 0), land.Trajectory.Heel);
        Near(new(1.2f, 0, 0), land.Trajectory.Toe);

        selected = PosePath.Blend([
            new(landing, new PoseWeightCurve(0.75f, 0.75f, 1)),
            new(turn, new PoseWeightCurve(0.25f, 0.25f, 1)),
        ]);
        request = new(selected, new PoseTimeOrigin(10, 0.25f, 0.5f), Conditions: 82);
        prediction = new(request, lookAhead: 2);
        Assert.True(prediction.TryGetNextContact(0, afterTime: 10, calculationTime: 10.1, out next));
        Assert.Equal(request, next.Prediction.Request);
        Assert.Equal(10.5, next.Prediction.Time, 6);
        Assert.Equal(0.5f, next.Prediction.PathTime);
        Assert.Equal(new(0.5f, 1, FootContactKind.Sliding), next.Contact);
        Near(new(0.5f, 0, 0), next.Trajectory.Root.Translation);
        Near(new(0.9811563f, 0, -0.19321585f), Vector3.TransformNormal(Vector3.UnitX, next.Trajectory.Root));
        Near(new(0.6717023f, 0, -0.03381277f), next.Trajectory.Heel);
        Near(new(1.0641649f, 0, -0.11109911f), next.Trajectory.Toe);
        var pose = new Matrix4x4[3];
        PosePredictionSample sampled = prediction.SamplePose(0, 10.5, calculationTime: 10.1, pose);
        Assert.Equal(next.Prediction, sampled);
        Near(next.Trajectory.Root.Translation, pose[0].Translation);
        Near(next.Trajectory.Heel, pose[1].Translation);
        Near(next.Trajectory.Toe, pose[2].Translation);
    }

    [Fact]
    public async Task TurningLoopsKeepCumulativeContactTimesAndCarryRootRotationIntoBothFootTrajectories()
    {
        // A repeating turn is an example of the shared cyclic path model.
        using var files = new ActionFiles();
        Dictionary<string, ImportedAction> actions = await files.ImportAsync();
        PosePath selected = PosePath.Loop(PosePath.FromClip(actions["Turn"].Clip, actions["Turn"].Motion));
        var request = new PosePredictionRequest(selected, new PoseTimeOrigin(100, 1.1f, 2), Conditions: 91);
        var prediction = new FootPrediction(request, lookAhead: 1.1f);
        Assert.True(prediction.TryGetNextContact(0, afterTime: 100, calculationTime: 100.01, out FootPredictionSample first));
        Assert.Equal(100.075, first.Prediction.Time, 6);
        Assert.Equal(new(1.25f, 1.75f, FootContactKind.ToePivot), first.Contact);
        Near(new(1, 0, -0.25f), first.Trajectory.Root.Translation);
        Near(new(-0.38268343f, 0, -0.9238795f), Vector3.TransformNormal(Vector3.UnitX, first.Trajectory.Root));
        Near(new(1.0765367f, 0, -0.0652241f), first.Trajectory.Heel);
        Near(new(0.9234633f, 0, -0.4347759f), first.Trajectory.Toe);
        var pose = new Matrix4x4[3];
        PosePredictionSample sampled = prediction.SamplePose(0, first.Prediction.Time, 100.01, pose);
        Assert.Equal(first.Prediction, sampled);
        Near(first.Trajectory.Root.Translation, pose[0].Translation);
        Near(first.Trajectory.Heel, pose[1].Translation);
        Near(first.Trajectory.Toe, pose[2].Translation);

        Assert.True(prediction.TryGetNextContact(0, afterTime: 100.55, calculationTime: 100.1, out FootPredictionSample second));
        Assert.Equal(100.575, second.Prediction.Time, 6);
        Assert.Equal(2.25f, second.Prediction.PathTime);
        Assert.Equal(new(2.25f, 2.75f, FootContactKind.ToePivot), second.Contact);
        Near(new(0.75f, 0, -1), second.Trajectory.Root.Translation);
        Near(new(-0.9238795f, 0, 0.38268343f), Vector3.TransformNormal(Vector3.UnitX, second.Trajectory.Root));
        Near(new(0.9347759f, 0, -1.0765367f), second.Trajectory.Heel);
        Near(new(0.5652241f, 0, -0.9234633f), second.Trajectory.Toe);
        sampled = prediction.SamplePose(0, second.Prediction.Time, 100.1, pose);
        Assert.Equal(second.Prediction, sampled);
        Near(second.Trajectory.Root.Translation, pose[0].Translation);
        Near(second.Trajectory.Heel, pose[1].Translation);
        Near(second.Trajectory.Toe, pose[2].Translation);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private sealed record ImportedAction(AnimationClip Clip, FootMotion Motion);

    private sealed class ActionFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "foot-motion-" + Guid.NewGuid().ToString("N"));
        private readonly string _source;

        public ActionFiles()
        {
            Directory.CreateDirectory(_directory);
            _source = Path.Combine(_directory, "actions.gltf");
            var bytes = new List<byte>();
            var accessors = new JsonArray();
            var views = new JsonArray();
            int Accessor(float[] values, string type, int components)
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

            int times = Accessor([0, 0.25f, 0.5f, 0.75f, 1], "SCALAR", 1);
            int rootTimes = Accessor([0, 1], "SCALAR", 1);
            int rootTranslation = Accessor([0, 0, 0, 1, 0, 0], "VEC3", 3);
            var animations = new JsonArray();
            JsonObject Action(string name, float[] heel, float[] toe, JsonArray markers)
            {
                int heelValues = Accessor(heel, "VEC3", 3);
                int toeValues = Accessor(toe, "VEC3", 3);
                return new JsonObject
                {
                    ["name"] = name,
                    ["samplers"] = new JsonArray(
                        new JsonObject { ["input"] = rootTimes, ["output"] = rootTranslation, ["interpolation"] = "LINEAR" },
                        new JsonObject { ["input"] = times, ["output"] = heelValues, ["interpolation"] = "LINEAR" },
                        new JsonObject { ["input"] = times, ["output"] = toeValues, ["interpolation"] = "LINEAR" }),
                    ["channels"] = new JsonArray(
                        new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = 0, ["path"] = "translation" } },
                        new JsonObject { ["sampler"] = 1, ["target"] = new JsonObject { ["node"] = 2, ["path"] = "translation" } },
                        new JsonObject { ["sampler"] = 2, ["target"] = new JsonObject { ["node"] = 3, ["path"] = "translation" } }),
                    ["extras"] = new JsonObject { ["someengine_id"] = name, ["someengine_foot_contacts"] = markers },
                };
            }
            animations.Add(Action("Attack",
                [-0.2f, -1, 0, -0.45f, -1, 0, -0.2f, 0, 0, 0.05f, 0, 0, -0.2f, 0, 0],
                [0.2f, -1, 0, -0.05f, -1, 0, 0.2f, 0, 0, 0.45f, 0, 0, 0.2f, 0, 0], []));
            JsonObject turn = Action("Turn",
                [-0.2f, -1, 0, -0.2f, -1, 0, -0.2f, -1, 0, -0.2f, -1, 0, -0.2f, -1, 0],
                [0.2f, -1, 0, 0.2f, -1, 0, 0.2f, -1, 0, 0.2f, -1, 0, 0.2f, -1, 0],
                new JsonArray(
                    new JsonObject { ["foot"] = 0, ["start"] = 0, ["end"] = 0.25f, ["kind"] = "Free" },
                    new JsonObject { ["foot"] = 0, ["start"] = 0.25f, ["end"] = 0.75f, ["kind"] = "ToePivot" }));
            int rotation = Accessor([0, 0, 0, 1, 0, 0.70710677f, 0, 0.70710677f], "VEC4", 4);
            turn["samplers"]!.AsArray().Add(new JsonObject
                { ["input"] = rootTimes, ["output"] = rotation, ["interpolation"] = "LINEAR" });
            turn["channels"]!.AsArray().Add(new JsonObject
                { ["sampler"] = 3, ["target"] = new JsonObject { ["node"] = 0, ["path"] = "rotation" } });
            animations.Add(turn);
            animations.Add(Action("Landing",
                [0.8f, 0, 0, 0.55f, -0.5f, 0, 0.3f, -1, 0, 0.05f, -1, 0, -0.2f, -1, 0],
                [1.2f, 0, 0, 0.95f, -0.5f, 0, 0.7f, -1, 0, 0.45f, -1, 0, 0.2f, -1, 0],
                new JsonArray(new JsonObject { ["foot"] = 0, ["start"] = 0.75f, ["end"] = 1, ["kind"] = "Sliding" })));
            JsonObject document = JsonNode.Parse("""
                {"asset":{"version":"2.0"},
                 "nodes":[{"name":"root","children":[1]},{"name":"hip","translation":[0,1,0],"children":[2,3]},
                    {"name":"heel","translation":[-0.2,-1,0]},{"name":"toe","translation":[0.2,-1,0]}],
                 "skins":[{"name":"Rig","joints":[2,3],"skeleton":0,
                    "extras":{"someengine_id":"rig","someengine_feet":[{"heel":2,"toe":3}]}}],
                 "scenes":[{"nodes":[0],"extras":{"someengine_id":"scene"}}],"scene":0}
                """)!.AsObject();
            document["animations"] = animations;
            document["bufferViews"] = views;
            document["accessors"] = accessors;
            document["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = bytes.Count,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()),
            });
            File.WriteAllText(_source, document.ToJsonString());
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
            metadata.Importers.Add(new(PoseSceneImporter.Id, default));
            metadata.Importers.Add(new(GltfSkeletonImporter.Id, default));
            metadata.Importers.Add(new(RawClipImporter.Id, JsonSerializer.SerializeToElement(
                new RawClipSettings(RootMotionNode: 0), RawClipSettingsJson.Default.RawClipSettings)));
            metadata.Importers.Add(new(FootMotionImporter.Id, JsonSerializer.SerializeToElement(
                new FootMotionSettings(SampleRate: 4, RootMotionNode: 0), FootMotionSettingsJson.Default.FootMotionSettings)));
            SourceMeta.Save(_source, metadata);
        }

        public async Task<Dictionary<string, ImportedAction>> ImportAsync()
        {
            var project = new AssetProject(_directory, []);
            var importer = new GltfSourceImporter(PoseTestAssets.AddAssetCapabilities(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
                PoseTestAssets.BuildVersion, PoseSceneImporter.Id);
            await project.ImportAsync(_source, importer);
            AssetEntry skeletonEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
            Skeleton skeleton;
            using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(_directory, skeletonEntry.Path)))
                skeleton = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();
            var actions = new Dictionary<string, ImportedAction>();
            foreach (AssetEntry entry in project.Manifest.Assets.Values.Where(value => value.AssetType == AnimationClipAsset.FileType))
            {
                using AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(Path.Combine(_directory, entry.Path));
                AnimationClip clip = (await asset.GetBlockAsync<RawClipBlock>()).CreateClip(skeleton);
                FootMotion motion = (await asset.GetBlockAsync<FootMotionBlock>()).CreateMotion();
                actions.Add(asset.Name, new(clip, motion));
            }
            return actions;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
