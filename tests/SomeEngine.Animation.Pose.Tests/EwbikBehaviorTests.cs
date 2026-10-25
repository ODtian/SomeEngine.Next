using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class EwbikBehaviorTests
{
    [Fact]
    public void WeightedSixDofAndIntermediateTargetsFollowAxisPrioritiesAcrossDampingSettingsAndCurrentAnimation()
    {
        // These two arm branches and an animated extra bone are example fixtures for general ancestor/effector binding.
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0, 5, 6, 7, 0],
        [
            Bone(Vector3.Zero), Bone(-Vector3.UnitX), Bone(-Vector3.UnitX), Bone(-Vector3.UnitX), Bone(Vector3.Zero),
            Bone(Vector3.UnitX), Bone(Vector3.UnitX), Bone(Vector3.UnitX), Bone(Vector3.Zero), Bone(Vector3.UnitZ),
        ], [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        var raw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1], [Vector4.Zero, new(.2f, 0, 0, 0)]),
            new(9, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1], [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
        ]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw));
        var rig = new EwbikRig(skeleton, 0, [new(4), new(8), new(2), new(4)],
            bones: [new(0, DampingScale: 0)], settings: new(Damping: .35f, Iterations: 192, PositionTolerance: .002f, OrientationTolerance: .002f));
        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance fast = storage.CreateInstance(skeleton), slow = storage.CreateInstance(skeleton);
        Entity fastTarget = PoseAnimation.CreateTarget(world, fast, 3, new(2));
        Entity slowTarget = PoseAnimation.CreateTarget(world, slow, 3, new(2));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(1))]), new(2), new(StageEnd.Settled)));
            builder.SolveEwbik(Entity.Null, new(rig, new(0), new(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new(0), Loop: true));
        }
        using CompiledGraph graph = builder.Compile();
        Entity fastAnimation = graph.Play(world, fastTarget), slowAnimation = graph.Play(world, slowTarget);
        world.ExecuteBufferWrite<EwbikInstance>(slowAnimation, static instances =>
            instances[0].Settings = new(Damping: .12f, Iterations: 576, PositionTolerance: .002f, OrientationTolerance: .002f));
        Quaternion left = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Quaternion right = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        EwbikTarget[] goals =
        [
            new(new(-1.95f, 1, 0), left, AxisPriorities: Vector3.One),
            new(new(2.05f, 1, 0), right, AxisPriorities: Vector3.One),
            new(new(-1.95f, 0, 0), right, AxisPriorities: Vector3.One),
            new(new(-1.95f, 1, 0), Quaternion.Identity, Weight: 0, AxisPriorities: Vector3.One),
        ];
        SetGoals(world, fastAnimation, goals); SetGoals(world, slowAnimation, goals);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => { nodes.Add(new PoseBlendSystem()); nodes.Add(new EwbikSystem()); });
        var context = new ImmediateSystemContext(world, 0, 0, .25);
        systems.Update(ref context);
        foreach (SkeletonInstance instance in new[] { fast, slow })
        {
            Near(new(-1.95f, 1, 0), instance.ModelPose.GetBone(4).Transform.Translation);
            Near(new(2.05f, 1, 0), instance.ModelPose.GetBone(8).Transform.Translation);
            Near(new(-1.95f, 0, 0), instance.ModelPose.GetBone(2).Transform.Translation);
            Near(Vector3.UnitY, Direction(instance, 4, Vector3.UnitX));
            Near(-Vector3.UnitX, Direction(instance, 4, Vector3.UnitY));
            Near(-Vector3.UnitY, Direction(instance, 8, Vector3.UnitX));
            Near(Vector3.UnitX, Direction(instance, 8, Vector3.UnitY));
            Near(-Vector3.UnitY, Direction(instance, 2, Vector3.UnitX));
            Near(new(.05f, 0, 0), instance.LocalPose[0].Translation);
            Near(new(.9238795f, .38268343f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[9].Rotation));
        }

        goals[0] = new(new(-1.9f, 1, 0), left, AxisPriorities: new(3, 0, 1));
        goals[1] = goals[1] with { Position = new(2.1f, 1, 0) };
        goals[2] = goals[2] with { Position = new(-1.9f, 0, 0) };
        goals[3] = new(new(-1.9f, 1, 0), Quaternion.Identity, AxisPriorities: new(0, 1, 1));
        SetGoals(world, fastAnimation, goals);
        goals[0] = goals[0] with { Weight = 3, AxisPriorities = Vector3.One };
        goals[3] = goals[3] with { AxisPriorities = Vector3.One };
        SetGoals(world, slowAnimation, goals);
        systems.Update(ref context);
        foreach (SkeletonInstance instance in new[] { fast, slow })
        {
            Near(new(-1.9f, 1, 0), instance.ModelPose.GetBone(4).Transform.Translation);
            Near(new(2.1f, 1, 0), instance.ModelPose.GetBone(8).Transform.Translation);
            Near(new(-1.9f, 0, 0), instance.ModelPose.GetBone(2).Transform.Translation);
            Near(new(.31622777f, .9486833f, 0), Direction(instance, 4, Vector3.UnitX));
            Near(Vector3.UnitZ, Direction(instance, 4, Vector3.UnitZ));
        }
        goals[0] = new(new(-1.85f, 1, 0), left, AxisPriorities: new(1, 0, 1));
        goals[1] = goals[1] with { Position = new(2.15f, 1, 0) };
        goals[2] = goals[2] with { Position = new(-1.85f, 0, 0) };
        goals[3] = new(new(-1.85f, 1, 0), Quaternion.Identity, AxisPriorities: new(0, 3, 1));
        SetGoals(world, fastAnimation, goals);
        goals[0] = goals[0] with { AxisPriorities = Vector3.One };
        goals[3] = goals[3] with { Weight = 3, AxisPriorities = Vector3.One };
        SetGoals(world, slowAnimation, goals);
        systems.Update(ref context);
        foreach (SkeletonInstance instance in new[] { fast, slow })
        {
            Near(new(-1.85f, 1, 0), instance.ModelPose.GetBone(4).Transform.Translation);
            Near(new(2.15f, 1, 0), instance.ModelPose.GetBone(8).Transform.Translation);
            Near(new(-1.85f, 0, 0), instance.ModelPose.GetBone(2).Transform.Translation);
            Near(new(.9486833f, .31622777f, 0), Direction(instance, 4, Vector3.UnitX));
            Near(new(.15f, 0, 0), instance.LocalPose[0].Translation);
            Assert.Equal(.75, instance.ModelPose.SampleTime);
        }
    }

    private static BoneTransform Bone(Vector3 translation) => new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    private static Vector3 Direction(SkeletonInstance instance, int bone, Vector3 axis) => Vector3.Normalize(Vector3.TransformNormal(axis, instance.ModelPose.GetBone(bone).Transform));
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < .004f, $"Expected {expected}; actual {actual}.");
    private static void SetGoals(World world, Entity animation, EwbikTarget[] goals) => world.ExecuteBufferWrite<EwbikInstance, EwbikTarget[]>(animation, ref goals,
        static (instances, ref goals) => goals.CopyTo(instances[0].Targets, 0));
}
