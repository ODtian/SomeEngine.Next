using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Features.Skinning;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task ActualMovementWarpsGpuStrideAndBodyWhileContactFeetKeepTheirTargetsAndHistory()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        // This bent-knee stance is an example with independently worked unit-length triangles.
        float sourceHeight = MathF.Sqrt(2);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 1, 5, 6, 1, 8],
            [Bone(Vector3.Zero), Bone(new(0, sourceHeight, 0)),
             Bone(Vector3.Zero), Bone(new(-.5f, -sourceHeight / 2, .5f)), Bone(new(-.5f, -sourceHeight / 2, -.5f)),
             Bone(Vector3.Zero), Bone(new(.5f, -sourceHeight / 2, .5f)), Bone(new(.5f, -sourceHeight / 2, -.5f)),
             Bone(new(0, .4f, 0)), Bone(new(0, .4f, 0))], [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        var source = new GpuPoseBlend(skeleton, new GpuBlendClip(QuantizedClipData.Encode(clip, 4), Loop: true));
        var legs = new GpuStrideLeg[]
        {
            new(new(skeleton, 2, 3, 4), 0), new(new(skeleton, 5, 6, 7), 1),
        };
        var rig = new GpuStrideRig(1, Vector3.UnitY, legs, [new(8, .5f), new(9, .5f)],
            minimumScale: .5f, maximumScale: 1.5f, maximumPelvisAdjustment: .5f);
        var pose = new GpuStridePose(source, rig);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        SkeletonInstance[] bodies = [first, second];
        Entity[] targets = new Entity[2];
        var reads = new List<GpuPoseReadback>();
        var expected = new Matrix4x4[3][][];
        for (int frame = 0; frame < 3; frame++) expected[frame] = [WorkedPose(frame, 0), WorkedPose(frame, 1)];
        var binding = new SkinBinding([Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(-1, 0, 0)], [4, 7]);
        SceneImage? actual = null;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                GpuPoseAnimation.AddSystems(frame.Systems.Animation);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<GpuStrideParameters, GpuStrideLeaf>(Entity.Null, new(pose));
                using CompiledGraph graph = builder.Compile();
                for (int actor = 0; actor < 2; actor++)
                {
                    targets[actor] = GpuPoseAnimation.CreateTarget(frame.World, bodies[actor]);
                    graph.Play(frame.World, targets[actor]);
                }
                foreach (Entity mesh in frame.Scene.MeshEntities)
                    frame.World.Add(mesh, new MeshSkin(frame.World.Read<WorldTransform>(mesh).Qvvs.Position.X > 0 ? second : first, binding));
            },
            UpdateFrame = frame =>
            {
                for (int actor = 0; actor < 2; actor++)
                {
                    float speed = frame.Index == 0 ? 1 : frame.Index == 1 && actor == 1 ? .5f : 1.5f;
                    Vector3 direction = frame.Index < 2 ? Vector3.UnitX : new(MathF.Sqrt(.5f), 0, (actor == 0 ? 1 : -1) * MathF.Sqrt(.5f));
                    GpuPoseTarget target = frame.World.Read<GpuPoseTarget>(targets[actor]);
                    // This example strides along +X while the source body faces +Z.
                    target.Movement = new(direction * speed, Vector3.UnitX, Vector3.UnitZ);
                    frame.World.Replace(targets[actor], target);
                    var contacts = new GpuPoseGoal[]
                    {
                        new() { Position = new(-1, 0, 0), PositionWeight = actor == 0 ? 1 : 0,
                            Orientation = Quaternion.Identity, OrientationWeight = actor == 0 ? 1 : 0, BendDirection = Vector3.UnitZ },
                        new() { Position = new(1, 0, 0), PositionWeight = actor == 1 ? 1 : 0,
                            Orientation = Quaternion.Identity, OrientationWeight = actor == 1 ? 1 : 0, BendDirection = Vector3.UnitZ },
                    };
                    frame.World.ExecuteBufferWrite<GpuPoseGoal, GpuPoseGoal[]>(targets[actor], ref contacts,
                        static (goals, ref contacts) => contacts.AsSpan().CopyTo(goals.AsSpan()));
                }
            },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 3,
            [new(1, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); }),
             new(2, edit => { foreach (Entity target in targets) edit.ReadGpuPose(target, reads.Add); edit.CaptureImage(image => actual = image); })]),
            "--fixed-step", "0.25", "--no-taa", "--no-vsm");
        Assert.Equal(4, reads.Count);
        for (int sample = 0; sample < 2; sample++)
        for (int actor = 0; actor < 2; actor++)
        {
            GpuPoseReadback result = reads[sample * 2 + actor];
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
            {
                Near(expected[sample + 1][actor][bone], result.Current.Span[bone], $"frame {sample + 1}, actor {actor}, bone {bone}, current");
                Near(expected[sample][actor][bone], result.Previous.Span[bone], $"frame {sample + 1}, actor {actor}, bone {bone}, previous");
            }
            foreach (GpuStrideLeg leg in legs)
            {
                Vector3 hip = result.Current.Span[leg.Chain.Root].Translation, knee = result.Current.Span[leg.Chain.Middle].Translation,
                    foot = result.Current.Span[leg.Chain.Tip].Translation;
                Assert.InRange(MathF.Abs(Vector3.Distance(hip, knee) - 1), 0, .001f);
                Assert.InRange(MathF.Abs(Vector3.Distance(knee, foot) - 1), 0, .001f);
            }
        }
        await content.WriteReferenceMeshesAsync((previous, right) =>
        {
            Matrix4x4[] models = expected[previous ? 1 : 2][right ? 1 : 0];
            return (binding.InverseBindMatrices[0] * models[4], binding.InverseBindMatrices[1] * models[7]);
        });
        await content.SetReferenceShaderAsync();
        SceneImage? reference = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(new ApplicationScene(content.Project.RootPath), 3,
            [new(2, edit => edit.CaptureImage(image => reference = image))]), "--fixed-step", "0.25", "--no-taa", "--no-vsm");
        SceneImage image = Assert.IsType<SceneImage>(reference);
        int visible = 0;
        foreach (byte value in image.Pixels.Span) if (value > 32 && value < 255) visible++;
        Assert.True(visible > 1000, $"Visible reference channels: {visible}");
        SceneImages.AssertEqualWithinPixel(image, Assert.IsType<SceneImage>(actual), tolerance: 2);

        Matrix4x4[] WorkedPose(int frame, int actor)
        {
            float height = frame == 0 || frame == 1 && actor == 1 ? sourceHeight : MathF.Sqrt(7) / 2;
            float yaw = frame < 2 ? 0 : (actor == 0 ? -1 : 1) * MathF.PI / 4;
            Matrix4x4 facing = Matrix4x4.CreateRotationY(yaw);
            Vector3 hip = new(0, height, 0);
            Vector3 left = new(-1, 0, 0), right = new(1, 0, 0);
            Vector3 leftKnee = new(-.5f, height / 2, .5f), rightKnee = new(.5f, height / 2, .5f);
            if (frame > 0 && actor == 0)
            {
                leftKnee.Z = MathF.Sqrt(5) / 4;
                right = Vector3.TransformNormal(new(1.5f, 0, 0), facing);
                rightKnee = right / 2 + hip / 2;
            }
            if (frame == 1 && actor == 1)
            {
                left = new(-.5f, 0, 0);
                leftKnee = new(-.25f, height / 2, MathF.Sqrt(7) / 4);
            }
            if (frame == 2 && actor == 1)
            {
                rightKnee.Z = MathF.Sqrt(5) / 4;
                left = Vector3.TransformNormal(new(-1.5f, 0, 0), facing);
                leftKnee = left / 2 + hip / 2;
            }
            var models = new Matrix4x4[10];
            models[0] = Matrix4x4.Identity;
            models[1] = At(facing, hip);
            WriteLeg(2, new(-.5f, -sourceHeight / 2, .5f), leftKnee, left, actor == 0);
            WriteLeg(5, new(.5f, -sourceHeight / 2, .5f), rightKnee, right, actor == 1);
            models[8] = At(Matrix4x4.CreateRotationY(yaw / 2), hip + new Vector3(0, .4f, 0));
            models[9] = At(Matrix4x4.Identity, hip + new Vector3(0, .8f, 0));
            return models;

            void WriteLeg(int root, Vector3 rest, Vector3 knee, Vector3 foot, bool contact)
            {
                Matrix4x4 upper = facing * Align(Vector3.TransformNormal(rest, facing), knee - hip);
                Matrix4x4 lower = upper * Align(Vector3.TransformNormal(new(rest.X, rest.Y, -rest.Z), upper), foot - knee);
                models[root] = At(upper, hip);
                models[root + 1] = At(lower, knee);
                models[root + 2] = At(contact ? Matrix4x4.Identity : facing, foot);
            }
        }
        static Matrix4x4 At(Matrix4x4 rotation, Vector3 position) { rotation.Translation = position; return rotation; }
        static void Near(Matrix4x4 expected, Matrix4x4 actual, string pose)
        {
            ReadOnlySpan<float> a = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in expected, 1));
            ReadOnlySpan<float> b = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in actual, 1));
            for (int index = 0; index < a.Length; index++)
                Assert.True(MathF.Abs(a[index] - b[index]) <= .001f, $"{pose}: expected {expected}; actual {actual}");
        }
        static Matrix4x4 Align(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from); to = Vector3.Normalize(to);
            return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new(Vector3.Cross(from, to), 1 + Vector3.Dot(from, to))));
        }
    }
}
