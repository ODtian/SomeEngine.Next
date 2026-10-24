using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task GpuBlendSpacesAndMaskedLayersMatchIndependentCpuGraphsInTheSameApplicationFrame()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        var skeleton = new Skeleton([-1, 0],
            [new() { Translation = new(.1f, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .1f), Scale = new(1.05f) },
             new() { Translation = new(0, .8f, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f), Scale = new(1.1f) }], [1, 2]);
        AnimationClip slow = BlendClip(1, 1), fast = BlendClip(2, 2), side = BlendClip(3, 1.5f);
        var line = new GpuBlendSpace(BlendSpace.From1D([new(0, slow), new(1, fast, .125f)]), 0, sampleRate: 8);
        var plane = new GpuBlendSpace(BlendSpace.From2D([new(new(0, 0), slow), new(new(1, 0), fast, .125f),
            new(new(0, 1), side, .25f)], [new(0, 1, 2)]), 0, 1, sampleRate: 8);
        QuantizedClipData upper = QuantizedClipData.Encode(BlendClip(4, 2), 8);
        var cover = new BoneMask([0, .7f]);
        var additive = new BoneMask([.25f, .5f]);
        var layered = new GpuBlendLayer(line, plane, cover, WeightParameter: 2);
        var definition = new GpuPoseBlend(skeleton,
            new GpuBlendLayer(layered, new GpuBlendClip(upper, Loop: true), additive, LayerMode.Additive, WeightParameter: 3));
        BlendSpace cpuLine = BlendSpace.From1D([new(0, line.Clips[0].CreateClip(skeleton)),
            new(1, line.Clips[1].CreateClip(skeleton), .125f)]);
        BlendSpace cpuPlane = BlendSpace.From2D([new(new(0, 0), plane.Clips[0].CreateClip(skeleton)),
            new(new(1, 0), plane.Clips[1].CreateClip(skeleton), .125f),
            new(new(0, 1), plane.Clips[2].CreateClip(skeleton), .25f)], [new(0, 1, 2)]);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        using SkeletonInstance cpuFirst = storage.CreateInstance(skeleton), cpuSecond = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [first, second], cpuBodies = [cpuFirst, cpuSecond];
        using var cpuWorld = new World();
        using var cpuSystems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(cpuWorld));
        PoseAnimation.AddSystems(cpuSystems, static composition =>
            AnimationSystems.Add(composition, new BlendSpaceSystem(), AnimationPhase.Composition),
            nodes: static nodes => nodes.Add(new PoseLayerSystem()));
        Entity[] cpuTargets = new Entity[2], cpuAnimations = new Entity[2];
        for (int character = 0; character < 2; character++)
        {
            Entity target = cpuTargets[character] = PoseAnimation.CreateTarget(cpuWorld, cpuBodies[character], 5, new(4));
            var layers = new AnimationLayers { Count = 3 };
            layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
            layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 1 };
            layers.Items[2] = new() { Mode = LayerMode.Additive, Weight = 1 };
            cpuWorld.Add(target, layers);
            using var builder = new AnimationBuilder(cpuWorld);
            builder.BlendPose1D(target, cpuLine, new(0), 0);
            builder.BlendPose2D(target, cpuPlane, new(1), 0, 1);
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(upper.CreateClip(skeleton), new(2), Loop: true));
            builder.LayerPose(target, new(new(0), new(1), new(3), cover), 1);
            builder.LayerPose(target, new(new(3), new(2), new(4), additive), 2);
            cpuAnimations[character] = builder.Play();
        }
        Entity[] targets = new Entity[2], animations = new Entity[2];
        var reads = new List<GpuPoseReadback>();
        var expectedCurrent = new List<Matrix4x4[][]>();
        var expectedPrevious = new List<Matrix4x4[][]>();
        var expectedLocal = new List<BoneTransform[][]>();
        Matrix4x4[][] prior = cpuBodies.Select(body => body.ModelPose.Transforms.ToArray()).ToArray();
        SceneImage? animated = null;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuBlendParameters, GpuBlendLeaf>(Entity.Null, new(definition));
                using CompiledGraph graph = builder.Compile();
                for (int character = 0; character < 2; character++)
                {
                    targets[character] = GpuPoseAnimation.CreateTarget(frame.World, bodies[character]);
                    animations[character] = graph.Play(frame.World, targets[character]);
                }
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [0, 1]);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? second : first, binding));
                }
            },
            UpdateFrame = frame =>
            {
                for (int character = 0; character < 2; character++)
                {
                    var inputs = new GraphParameters();
                    inputs.Values[0] = character == 0 ? (frame.Index < 2 ? .2f : .8f) : (frame.Index < 2 ? .7f : .1f);
                    inputs.Values[1] = character == 0 ? .1f : .6f;
                    inputs.Values[2] = character == 0 ? .8f : .3f;
                    inputs.Values[3] = character == 0 ? .35f : .7f;
                    frame.World.Replace(animations[character], inputs);
                    cpuWorld.Replace(cpuAnimations[character], inputs);
                    AnimationLayers layers = cpuWorld.Read<AnimationLayers>(cpuTargets[character]);
                    layers.Items[1].Weight = inputs.Values[2];
                    layers.Items[2].Weight = inputs.Values[3];
                    cpuWorld.Replace(cpuTargets[character], layers);
                }
                var context = new ImmediateSystemContext(cpuWorld, 0, 0, .25);
                cpuSystems.Update(ref context);
                Matrix4x4[][] current = cpuBodies.Select(body => body.ModelPose.Transforms.ToArray()).ToArray();
                expectedCurrent.Add(current);
                expectedPrevious.Add(prior);
                expectedLocal.Add(cpuBodies.Select(body => body.LocalPose.ToArray()).ToArray());
                prior = current;
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 4,
            [new(0, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(2, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(3, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); edit.CaptureImage(image => animated = image); })]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        Assert.Equal(6, reads.Count);
        int[] frames = [0, 2, 3];
        for (int sample = 0; sample < frames.Length; sample++)
        for (int character = 0; character < 2; character++)
        for (int bone = 0; bone < 2; bone++)
        {
            GpuPoseReadback result = reads[sample * 2 + character];
            GpuPoseExamples.Near(expectedCurrent[frames[sample]][character][bone], result.Current.Span[bone]);
            if (frames[sample] > 0)
                GpuPoseExamples.Near(expectedPrevious[frames[sample]][character][bone], result.Previous.Span[bone]);
        }
        Assert.True(Vector3.Distance(reads[4].Current.Span[0].Translation, reads[5].Current.Span[0].Translation) > .01f);
        var referenceApplication = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)], [0, 1]);
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? cpuSecond : cpuFirst, binding));
                }
            },
            UpdateFrame = frame =>
            {
                for (int character = 0; character < 2; character++)
                    cpuBodies[character].Update(expectedLocal[frame.Index][character], .25 * (frame.Index + 1));
            },
        };
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(referenceApplication, 4,
            [new(3, edit => edit.CaptureImage(image => reference = image))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        SceneImage expected = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in expected.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
        SceneImages.AssertEqualWithinPixel(expected, Assert.IsType<SceneImage>(animated), tolerance: 2);

        AnimationClip BlendClip(int factor, float duration) => new(skeleton, duration,
            ClipSampler.Create<BlendCurve, BlendCurveSampler>(new(factor, skeleton)));
    }

    private sealed record BlendCurve(int Factor, Skeleton Skeleton);
    private readonly struct BlendCurveSampler : IClipSampler<BlendCurve>
    {
        public static void SampleLocal(BlendCurve data, float time, Span<BoneTransform> destination)
        {
            for (int bone = 0; bone < destination.Length; bone++)
            {
                BoneTransform reference = data.Skeleton.ReferencePose[bone];
                destination[bone] = new()
                {
                    Translation = reference.Translation + new Vector3(.1f * data.Factor * time, .04f * (bone + 1) * data.Factor * time, 0),
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .15f * data.Factor * time) * reference.Rotation,
                    Scale = reference.Scale * new Vector3(1 + .03f * data.Factor * time, 1 + .02f * time, 1),
                };
            }
        }
    }
}
