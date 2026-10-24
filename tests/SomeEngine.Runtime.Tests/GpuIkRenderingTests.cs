using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task GpuTwoBoneAndLimitedAimFollowIndependentModelGoalsAndPresentTheirAdjustedHistory()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0, 5, 6, 7],
            [Bone(Vector3.Zero), Bone(Vector3.Zero), Bone(Vector3.UnitY), Bone(Vector3.UnitY), Bone(new(0, .25f, 0)),
             Bone(Vector3.UnitY), Bone(new(0, .5f, 0)), Bone(new(0, .5f, 0)), Bone(new(0, .3f, 0))],
            [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var raw = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        QuantizedClipData clip = QuantizedClipData.Encode(raw, 4);
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        var leg = new GpuTwoBoneChain(skeleton, 1, 2, 3, maximumBend: MathF.PI / 2);
        var upper = new GpuAimChain(skeleton,
            [new(5, Vector3.UnitY, .25f, MathF.PI / 4), new(6, Vector3.UnitY, .5f, MathF.PI / 4),
             new(7, Vector3.UnitY, 1, MathF.PI / 4)]);
        var pose = new GpuAimPose(new GpuTwoBonePose(source, leg, 0), upper, 1);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        using SkeletonInstance referenceFirst = storage.CreateInstance(skeleton), referenceSecond = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [first, second], referenceBodies = [referenceFirst, referenceSecond];
        Entity[] targets = new Entity[2];
        var reads = new List<GpuPoseReadback>();
        var expected = new List<Matrix4x4[][]>();
        var locals = new List<BoneTransform[][]>();
        var binding = new SkinBinding([Matrix4x4.CreateTranslation(0, -2, 0), Matrix4x4.CreateTranslation(0, -1, 0)], [3, 5]);
        SceneImage? animated = null;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuAimParameters, GpuAimLeaf>(Entity.Null, new(pose, TranslationBone: 0));
                using CompiledGraph graph = builder.Compile();
                for (int character = 0; character < 2; character++)
                {
                    targets[character] = GpuPoseAnimation.CreateTarget(frame.World, bodies[character]);
                    graph.Play(frame.World, targets[character]);
                }
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? second : first, binding));
                }
            },
            UpdateFrame = frame =>
            {
                var frameLocals = new BoneTransform[2][];
                for (int character = 0; character < 2; character++)
                {
                    float sign = (character == 0 ? 1 : -1) * (frame.Index < 2 ? 1 : -1);
                    float height = character == 0 ? .2f : -.1f;
                    float aimWeight = character == 0 ? 1 : .5f;
                    GpuPoseTarget output = frame.World.Read<GpuPoseTarget>(targets[character]);
                    output.ModelOffset = new(0, height, 0);
                    frame.World.Replace(targets[character], output);
                    var goals = new GpuPoseGoal[]
                    {
                        new() { Position = new(sign, character == 0 ? 1 + height : height - 1, 0),
                            PositionWeight = character == 0 ? 1 : .5f, BendDirection = new(sign, 0, 0),
                            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, character == 0 ? MathF.PI / 6 : MathF.PI / 3),
                            OrientationWeight = character == 0 ? 1 : .5f },
                        new() { Position = new(100 * sign, 1 + height, 0), PositionWeight = aimWeight },
                    };
                    frame.World.ExecuteBufferWrite<GpuPoseGoal, GpuPoseGoal[]>(targets[character], ref goals,
                        static (values, ref goals) => goals.AsSpan().CopyTo(values.AsSpan()));

                    BoneTransform[] local = skeleton.ReferencePose.ToArray();
                    local[0].Translation.Y = height;
                    // The second weighted goal is closer than the 90-degree bend limit permits; both worked results form a right angle.
                    local[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sign * MathF.PI / 2);
                    local[2].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, sign * MathF.PI / 2);
                    local[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
                    local[5].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sign * MathF.PI / 4 * .25f * aimWeight);
                    local[6].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sign * MathF.PI / 4 * .5f * aimWeight);
                    local[7].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sign * MathF.PI / 4 * aimWeight);
                    referenceBodies[character].Update(local, frame.DeltaSeconds * (frame.Index + 1));
                    frameLocals[character] = local;
                }
                locals.Add(frameLocals);
                expected.Add(referenceBodies.Select(body => body.ModelPose.Transforms.ToArray()).ToArray());
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 3,
            [new(1, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(2, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); edit.CaptureImage(image => animated = image); })]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        Assert.Equal(4, reads.Count);
        for (int sample = 0; sample < 2; sample++)
        for (int character = 0; character < 2; character++)
        {
            GpuPoseReadback result = reads[sample * 2 + character];
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
            {
                GpuPoseExamples.Near(expected[sample + 1][character][bone], result.Current.Span[bone]);
                GpuPoseExamples.Near(expected[sample][character][bone], result.Previous.Span[bone]);
            }
            Vector3 root = result.Current.Span[1].Translation, middle = result.Current.Span[2].Translation,
                tip = result.Current.Span[3].Translation;
            Assert.InRange(MathF.Abs(Vector3.Distance(root, middle) - 1), 0, .001f);
            Assert.InRange(MathF.Abs(Vector3.Distance(middle, tip) - 1), 0, .001f);
            float sign = (character == 0 ? 1 : -1) * (sample == 0 ? 1 : -1);
            Assert.InRange(Vector3.Distance(new(sign, character == 0 ? 1.2f : .9f, 0), tip), 0, .001f);
            Assert.True(sign * (middle.X - root.X) > .99f);
            Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, result.Current.Span[7]));
            float angle = MathF.PI / 4 * 1.75f * (character == 0 ? 1 : .5f);
            Assert.InRange(Vector3.Distance(new(sign * MathF.Sin(angle), MathF.Cos(angle), 0), forward), 0, .001f);
        }
        var referenceApplication = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                foreach (Entity mesh in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0;
                    frame.World.Add(mesh, new MeshSkin(right ? referenceSecond : referenceFirst, binding));
                }
            },
            UpdateFrame = frame =>
            {
                for (int character = 0; character < 2; character++)
                    referenceBodies[character].Update(locals[frame.Index][character], frame.DeltaSeconds * (frame.Index + 1));
            },
        };
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(referenceApplication, 3,
            [new(2, edit => edit.CaptureImage(image => reference = image))]), "--no-taa", "--no-vsm", "--fixed-step", "0.25");
        SceneImage image = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in image.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000);
        SceneImages.AssertEqualWithinPixel(image, Assert.IsType<SceneImage>(animated), tolerance: 2);
    }
}
