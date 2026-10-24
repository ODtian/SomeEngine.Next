using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task LearnedFlowModelGeneratesIndependentControlledPosesThroughTheCommonGraphAndCluster()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../samples/SomeEngine.Animation.FlowMatching/example.flow.json"));
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1], [identity, identity,
            new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2, 3]);
        Guid skeletonId = Guid.NewGuid();
        using (var asset = new SkeletonAsset(skeletonId))
        {
            asset.SetBlock(new SkeletonBlock(skeleton.Parents, skeleton.ReferencePose, skeleton.NameHashes));
            content.Project.RegisterAsset(await asset.SaveAsync(Path.Combine(content.Project.RootPath, "assets", "example.skeleton.asset")));
        }
        string exportPath = Path.Combine(content.Project.RootPath, "assets", "example.flow.json");
        File.Copy(source, exportPath);
        var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
        metadata.Importers.Add(new(FlowMatchingImporter.Id, JsonSerializer.SerializeToElement(
            new FlowMatchingSettings(skeletonId), FlowMatchingSettingsJson.Default.FlowMatchingSettings)));
        SourceMeta.Save(exportPath, metadata);
        var importer = new FlowMatchingSourceImporter(FlowModelTestAssets.AddAssetCapabilities(new ServiceCollection()));
        Guid modelId = Assert.Single(await content.Project.ImportAsync(exportPath, importer));
        FlowMatchingModel model;
        using (AnimationModelAsset asset = await AnimationModelAsset.OpenAsync(
            Path.Combine(content.Project.RootPath, content.Project.Manifest.Assets[modelId].Path)))
        {
            FlowMatchingBlock block = await asset.GetBlockAsync<FlowMatchingBlock>();
            Assert.Equal(skeletonId, block.Skeleton.Value);
            model = block.CreateModel();
        }
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [first, second];
        Entity[] animations = new Entity[2];
        var produced = new FlowPoseReads(bodies, animations);
        SceneImage? actual = null;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                PoseAnimation.AddSystems(frame.Systems.Animation, nodes: static nodes =>
                {
                    nodes.Add(new FlowMatchingSystem());
                    nodes.Add(new PoseBlendSystem());
                });
                AnimationSystems.Add(frame.Systems.Animation, produced, AnimationPhase.Completion);
                using var builder = new AnimationBuilder(frame.World);
                using (builder.Parallel())
                {
                    builder.Leaf<FlowMatchingParameters, FlowMatchingLeaf>(Entity.Null,
                        new(model, new(0), NoiseParameter: 6));
                    builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(0))]), new(1), new(StageEnd.Settled)));
                }
                using CompiledGraph graph = builder.Compile();
                for (int actor = 0; actor < 2; actor++)
                    animations[actor] = graph.Play(frame.World, PoseAnimation.CreateTarget(frame.World, bodies[actor], 2, new(1)));
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [1, 2]);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0 ? second : first, binding));
            },
            UpdateFrame = frame =>
            {
                Vector3[][] controls = [[new(.7f, .1f, 0), new(.9f, .2f, 0)],
                    [new(-.65f, -.2f, 0), new(-.8f, -.5f, 0)]];
                float[][] noise = [[.2f, -.3f, .1f, .4f, -.2f, .3f], [-.1f, .4f, -.3f, .1f, .2f, -.4f],
                    [.3f, .1f, -.2f, -.1f, .4f, .2f]];
                for (int actor = 0; actor < 2; actor++)
                {
                    var parameters = new GraphParameters();
                    for (int value = 0; value < 6; value++)
                        parameters.Values[6 + value] = noise[frame.Index][value];
                    frame.World.Replace(animations[actor], parameters);
                    Vector3[] control = controls[actor];
                    frame.World.ExecuteBufferWrite<FlowMatchingInstance, Vector3[]>(animations[actor], ref control,
                        static (instances, ref control) =>
                        {
                            ref FlowMatchingInstance instance = ref instances[0];
                            ReadOnlySpan<FlowControlField> fields = instance.Definition.Parameters[instance.Index].Model.Controls;
                            fields[0].Encode(control[0], instance.State.Controls);
                            fields[1].Encode(control[1], instance.State.Controls);
                        });
                }
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 3,
            [new(2, edit => edit.CaptureImage(value => actual = value))]), "--fixed-step", "0.1", "--no-taa", "--no-vsm");
        BoneTransform[][] final = produced.Values[^1];
        Assert.True(final[0][0].Translation.X > .01f);
        Assert.True(final[1][0].Translation.X < -.01f);
        Assert.True(final[0][1].Rotation.Z > .05f);
        Assert.True(final[1][1].Rotation.Z < -.05f);
        // Fixed example output from the independent NumPy evaluator and the learned export.
        Matrix4x4[][][] numpyFrames =
        [
            [
                [
                    new(0.999984622f, 0.00554775959f, 0f, 0f,
                        -0.00554775959f, 0.999984622f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0.0115736499f, 0.000435302412f, 0f, 1f),
                    new(0.983564258f, 0.180558637f, 0f, 0f,
                        -0.180558637f, 0.983564198f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0.0296226144f, 0.00805437285f, 0f, 1f),
                    new(0.997609317f, -0.0691064671f, 0f, 0f,
                        0.0691064745f, 0.997609377f, 0f, 0f,
                        0f, 0f, 1.00000012f, 0f,
                        0f, 1f, 0f, 1f)
                ],
                [
                    new(0.999988675f, -0.00475491118f, 0f, 0f,
                        0.00475491118f, 0.999988675f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        -0.0107172774f, -0.000849588308f, 0f, 1f),
                    new(0.991524756f, -0.129917577f, 0f, 0f,
                        0.129917577f, 0.991524816f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        -0.0260519199f, 0.00825549848f, 0f, 1f),
                    new(0.984033287f, -0.177984849f, 0f, 0f,
                        0.177984834f, 0.984033227f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0f, 1f, 0f, 1f)
                ]
            ],
            [
                [
                    new(0.999889135f, 0.0148897273f, 0f, 0f,
                        -0.0148897273f, 0.999889135f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0.029938912f, 0.00124562893f, 0f, 1f),
                    new(0.951367438f, 0.308058411f, 0f, 0f,
                        -0.308058381f, 0.951367497f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0.046416644f, 0.015888365f, 0f, 1f),
                    new(0.999052882f, -0.0435131863f, 0f, 0f,
                        0.0435131863f, 0.999052882f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0f, 1f, 0f, 1f)
                ],
                [
                    new(0.99991405f, -0.0131119657f, 0f, 0f,
                        0.0131119657f, 0.99991405f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        -0.027714286f, -0.00202170247f, 0f, 1f),
                    new(0.975978792f, -0.217865556f, 0f, 0f,
                        0.217865556f, 0.975978792f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        -0.0419344082f, 0.0164531805f, 0f, 1f),
                    new(0.973356247f, -0.229298323f, 0f, 0f,
                        0.229298294f, 0.973356128f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0f, 1f, 0f, 1f)
                ]
            ],
            [
                [
                    new(0.999636352f, 0.0269648731f, 0f, 0f,
                        -0.0269648731f, 0.999636352f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0.0522841364f, 0.00251809321f, 0f, 1f),
                    new(0.91653198f, 0.399961442f, 0f, 0f,
                        -0.399961442f, 0.91653192f, 0f, 0f,
                        0f, 0f, 0.99999994f, 0f,
                        0.0562506542f, 0.023507053f, 0f, 1f),
                    new(0.999781191f, -0.020914441f, 0f, 0f,
                        0.0209144428f, 0.999781311f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        0f, 1f, 0f, 1f)
                ],
                [
                    new(0.999710381f, -0.0240653474f, 0f, 0f,
                        0.0240653474f, 0.999710381f, 0f, 0f,
                        0f, 0f, 1f, 0f,
                        -0.0485415235f, -0.00324529642f, 0f, 1f),
                    new(0.961099684f, -0.276201844f, 0f, 0f,
                        0.276201844f, 0.961099684f, 0f, 0f,
                        0f, 0f, 1.00000012f, 0f,
                        -0.0516992472f, 0.0244526658f, 0f, 1f),
                    new(0.964810491f, -0.26294592f, 0f, 0f,
                        0.26294595f, 0.96481061f, 0f, 0f,
                        0f, 0f, 0.99999994f, 0f,
                        0f, 1f, 0f, 1f)
                ]
            ]
        ];
        Matrix4x4[][] numpy = numpyFrames[^1];
        float[][] numpyHistory =
        [
            [0.849328578f, 1.55074096f, 1.02812171f, 0.197674602f, 1.08379519f, 0.202203691f],
            [0.850642681f, 1.59466362f, -0.878517807f, -0.229421899f, -0.942422152f, -0.656648874f],
        ];
        float error = 0;
        for (int actor = 0; actor < 2; actor++)
        {
            for (int value = 0; value < 6; value++)
                error = Math.Max(error, Math.Abs(numpyHistory[actor][value] - produced.Histories[^1][actor][value]));
            for (int bone = 0; bone < 3; bone++)
            {
                Matrix4x4 expectedMatrix = numpy[actor][bone], actualMatrix = final[actor][bone].ToMatrix();
                ReadOnlySpan<float> expectedValues = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in expectedMatrix, 1));
                ReadOnlySpan<float> actualValues = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in actualMatrix, 1));
                for (int value = 0; value < 16; value++) error = Math.Max(error, Math.Abs(expectedValues[value] - actualValues[value]));
            }
        }
        _output.WriteLine($"Flow NumPy parity maximum error: {error:G9}; tolerance: 0.0002");
        Assert.InRange(error, 0, .0002f);
        using SkeletonInstance referenceFirst = storage.CreateInstance(skeleton), referenceSecond = storage.CreateInstance(skeleton);
        SkeletonInstance[] references = [referenceFirst, referenceSecond];
        SceneImage? expected = null;
        var referenceApplication = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [1, 2]);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0
                        ? referenceSecond : referenceFirst, binding));
            },
            UpdateFrame = frame =>
            {
                for (int actor = 0; actor < 2; actor++)
                {
                    var transforms = new BoneTransform[3];
                    for (int bone = 0; bone < 3; bone++)
                    {
                        Matrix4x4.Decompose(numpyFrames[frame.Index][actor][bone],
                            out Vector3 scale, out Quaternion rotation, out Vector3 position);
                        transforms[bone] = new() { Translation = position, Rotation = rotation, Scale = scale };
                    }
                    references[actor].Update(transforms, (frame.Index + 1) * .1);
                }
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(referenceApplication, 3,
            [new(2, edit => edit.CaptureImage(value => expected = value))]), "--fixed-step", "0.1", "--no-taa", "--no-vsm");
        SceneImage reference = Assert.IsType<SceneImage>(expected);
        int visible = 0;
        foreach (byte value in reference.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
        SceneImages.AssertEqualWithinPixel(reference, Assert.IsType<SceneImage>(actual), tolerance: 2);
    }

    private sealed class FlowPoseReads(SkeletonInstance[] bodies, Entity[] animations) : ISystem<ImmediateSystemContext>
    {
        internal readonly List<BoneTransform[][]> Values = [];
        internal readonly List<float[][]> Histories = [];
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Values.Add(bodies.Select(body => body.LocalPose.ToArray()).ToArray());
            float[][] histories = new float[animations.Length][];
            for (int actor = 0; actor < animations.Length; actor++)
            {
                int index = actor;
                context.World.ExecuteBufferRead<FlowMatchingInstance>(animations[actor],
                    instances => histories[index] = instances.Read(0).State.History.ToArray());
            }
            Histories.Add(histories);
        }
    }
}

[AssetCapabilities]
public static partial class FlowModelTestAssets;
