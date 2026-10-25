using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PhysicsPoseBehaviorTests
{
    private static readonly Channel<float> PhysicsWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);

    [Fact]
    public void CompletedBodyPosesFollowTheirBoneChainAndPartWeightsWhileRecoveryReturnsToTheCurrentAnimation()
    {
        var skeleton = new Skeleton([-1, 0, -1, 2],
        [
            Transform(Vector3.Zero, 0), Transform(Vector3.UnitY, 0),
            Transform(Vector3.Zero, MathF.PI / 2), Transform(-Vector3.UnitY, 0),
        ], [1, 2, 3, 4]);
        var mapping = new PhysicsPoseMap(skeleton,
        [
            new(1, Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 1, 0)),
            new(3, Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-1, 0, 0)),
            new(0, Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 0, 0)),
        ]);
        var clip = new AnimationClip(skeleton, 4, ClipSampler.Create<Skeleton, MovingSampler>(skeleton));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 3, output: new(2));
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 1 };
        world.Add(target, layers);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(target, new(new PoseBlend([new(new(1))]), new(2), new(StageEnd.Settled)));
            builder.BlendPhysicsPose(target, new(new(0), new(1), mapping, new BoneMask([1, .5f, 0, 1])), layer: 1);
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0), Loop: true));
        }
        Entity animation = builder.Play();
        world.ExecuteBufferWrite<PhysicsPoseInstance>(animation, static instances =>
        {
            ref PhysicsPoseInstance input = ref instances[0];
            input.ActorTransform = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 0);
            input.BodyTransforms[0] = Matrix4x4.CreateRotationX(MathF.PI / 2) *
                Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(8, 22, 0);
            input.BodyTransforms[1] = Matrix4x4.CreateRotationY(MathF.PI / 2) *
                Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(9, 26, 0);
            input.BodyTransforms[2] = Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(9, 22, 0);
        });
        var recovery = Spec.Tween<float, FloatOps, Linear>(1);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, recovery);
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new PoseBlendSystem());
            nodes.Add(new PhysicsPoseSystem());
        });

        Tick(systems, world, 0);
        Near(new(2, 0, 0), instance.LocalPose[0].Translation);
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new(1.5f, .5f, 0), instance.LocalPose[1].Translation);
        Near(new(.70710677f, .70710677f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[1].Rotation));
        Near(new(1.5f, 1.5f, 0), instance.ModelPose.Transforms[1].Translation);
        Near(new(-.70710677f, .70710677f, 0), Vector3.TransformNormal(Vector3.UnitX, instance.ModelPose.Transforms[1]));
        Near(new(2, -6, 0), instance.LocalPose[3].Translation);
        Near(new(6, 2, 0), instance.ModelPose.Transforms[3].Translation);

        PropertyAnimation.To(world, target, PhysicsWeight, 0f, recovery);
        Tick(systems, world, .0001);
        Assert.InRange(Vector3.Distance(new(2, 0, 0), instance.LocalPose[0].Translation), 0, .001f);
        Assert.InRange(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation)), 0, .001f);
        Tick(systems, world, .4999);
        Near(new(1.25f, 0, 0), instance.LocalPose[0].Translation);
        Near(new(.70710677f, .70710677f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new(.75f, .75f, 0), instance.LocalPose[1].Translation);
        Near(new(.75f, -3.5f, 0), instance.LocalPose[3].Translation);
        Near(new(3.5f, 1.25f, 0), instance.ModelPose.Transforms[3].Translation);

        Tick(systems, world, .5);
        Near(new(1, 0, 0), instance.LocalPose[0].Translation);
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(Vector3.UnitY, instance.LocalPose[1].Translation);
        Near(-Vector3.UnitY, instance.LocalPose[3].Translation);
        Near(new(1, 1, 0), instance.ModelPose.Transforms[1].Translation);
        Near(new(1, 1, 0), instance.ModelPose.Transforms[3].Translation);
        Assert.Equal(1, instance.ModelPose.SampleTime, 5);
        Tick(systems, world, .5);
        Near(new(1.5f, 0, 0), instance.LocalPose[0].Translation);
        Near(new(1, 1.5f, 0), instance.ModelPose.Transforms[3].Translation);
    }

    private static BoneTransform Transform(Vector3 translation, float radians) => new()
    {
        Translation = translation, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians), Scale = Vector3.One,
    };

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < .00002f, $"Expected {expected}; actual {actual}.");

    private readonly struct MovingSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination)
        {
            skeleton.ReferencePose.CopyTo(destination);
            destination[0].Translation = new(time, 0, 0);
            destination[2].Translation = new(0, time, 0);
        }
    }
}
