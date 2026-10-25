using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PbikBehaviorTests
{
    [Fact]
    public void WholeBodyTargetsMoveThePelvisAndLimbsWithinJointLimitsWhileAnimationAndTargetsAdvance()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Skeleton skeleton = CreateBody();
        var raw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 0), new(.2f, 0, 0, 0)]),
            new(14, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 1],
                [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
        ]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw));
        var rig = new PbikRig(skeleton, root: 0, effectors: [4, 7, 10, 13],
            settings:
            [
                new(2, PreferredAngles: new(0, 0, MathF.PI / 6), PreferredWeight: 1),
                new(3, PreferredAngles: new(0, 0, -MathF.PI / 3), PreferredWeight: 1),
                new(5, PreferredAngles: new(0, 0, -MathF.PI / 6), PreferredWeight: 1),
                new(6, PreferredAngles: new(0, 0, MathF.PI / 3), PreferredWeight: 1),
                new(8, PreferredAngles: new(0, 0, MathF.PI / 6), PreferredWeight: 1),
                new(9, PreferredAngles: new(0, 0, -MathF.PI / 3), PreferredWeight: 1),
                new(11, PreferredAngles: new(0, 0, -MathF.PI / 6), PreferredWeight: 1),
                new(12, PreferredAngles: new(0, 0, MathF.PI / 3), PreferredWeight: 1),
            ],
            limits:
            [
                new(3, new(0, 0, -MathF.PI / 2), Vector3.Zero),
                new(6, Vector3.Zero, new(0, 0, MathF.PI / 2)),
                new(9, new(0, 0, -MathF.PI / 2), Vector3.Zero),
                new(12, Vector3.Zero, new(0, 0, MathF.PI / 2)),
            ], tolerance: .001f, iterations: 256);
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new PoseResultId(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.SolvePbik(target, new(rig, new PoseResultId(0), new PoseResultId(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new PoseResultId(0), Loop: true));
        }
        Entity animation = builder.Play();
        PbikTarget[] goals =
        [
            new(new(-1.2320508f, 1.5f, 0)), new(new(3.2320508f, 1.5f, 0)),
            new(new(.5f, -1.7320508f, 0)), new(new(1.5f, -1.7320508f, 0)),
        ];
        world.ExecuteBufferWrite<PbikInstance, PbikTarget[]>(animation, ref goals,
            static (instances, ref goals) => goals.CopyTo(instances[0].Targets, 0));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PbikSystem()));
        var context = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref context);
        for (int effector = 0; effector < goals.Length; effector++)
            Near(goals[effector].Position, instance.ModelPose.GetBone(rig.Effectors[effector]).Transform.Translation,
                rig.Tolerance + .00001f);
        Assert.True(instance.ModelPose.GetBone(0).Transform.Translation.X > .1f);
        foreach (int bone in new[] { 3, 6, 9, 12 })
        {
            float angle = AngleZ(instance.LocalPose[bone].Rotation);
            Assert.InRange(angle, bone is 3 or 9 ? -MathF.PI / 2 - .0001f : -.0001f,
                bone is 3 or 9 ? .0001f : MathF.PI / 2 + .0001f);
            Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, instance.LocalPose[bone].Rotation), .0001f);
        }
        Assert.True(AngleZ(instance.LocalPose[9].Rotation) < -.1f);
        Assert.True(AngleZ(instance.LocalPose[12].Rotation) > .1f);
        foreach (int bone in new[] { 3, 4, 6, 7, 9, 10, 12, 13 })
            Assert.InRange(Vector3.Distance(instance.ModelPose.GetBone(bone).Transform.Translation,
                instance.ModelPose.GetBone(skeleton.Parents[bone]).Transform.Translation), .9999f, 1.0001f);
        Assert.InRange(AngleZ(instance.LocalPose[14].Rotation), MathF.PI / 8 - .0001f, MathF.PI / 8 + .0001f);

        goals[0] = new(goals[0].Position + new Vector3(.1f, 0, 0));
        world.ExecuteBufferWrite<PbikInstance, PbikTarget[]>(animation, ref goals,
            static (instances, ref goals) => goals.CopyTo(instances[0].Targets, 0));
        systems.Update(ref context);
        for (int effector = 0; effector < goals.Length; effector++)
            Near(goals[effector].Position, instance.ModelPose.GetBone(rig.Effectors[effector]).Transform.Translation,
                rig.Tolerance + .00001f);
        Assert.InRange(AngleZ(instance.LocalPose[14].Rotation), MathF.PI / 4 - .0001f, MathF.PI / 4 + .0001f);
        Assert.Equal(.5, instance.ModelPose.SampleTime, 5);
    }

    [Fact]
    public void TargetWeightsAndPelvisStiffnessDistributeWholeBodyMotionFromTheCurrentAnimationPose()
    {
        using var world = new World();
        Skeleton skeleton = CreateBody();
        var raw = new RawClipData(1,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, .5f, 1],
                [new(0, 0, 0, 0), new(0, 0, 0, 0), new(.4f, 0, 0, 0)]),
        ]);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(raw));
        PbikBoneSettings[] settings =
        [
            new(2, PreferredAngles: new(0, 0, MathF.PI / 6), PreferredWeight: 1),
            new(3, PreferredAngles: new(0, 0, -MathF.PI / 3), PreferredWeight: 1),
            new(5, PreferredAngles: new(0, 0, -MathF.PI / 6), PreferredWeight: 1),
            new(6, PreferredAngles: new(0, 0, MathF.PI / 3), PreferredWeight: 1),
            new(8, PreferredAngles: new(0, 0, MathF.PI / 6), PreferredWeight: 1),
            new(9, PreferredAngles: new(0, 0, -MathF.PI / 3), PreferredWeight: 1),
            new(11, PreferredAngles: new(0, 0, -MathF.PI / 6), PreferredWeight: 1),
            new(12, PreferredAngles: new(0, 0, MathF.PI / 3), PreferredWeight: 1),
        ];
        PbikJointLimit[] limits =
        [
            new(3, new(0, 0, -MathF.PI / 2), Vector3.Zero),
            new(6, Vector3.Zero, new(0, 0, MathF.PI / 2)),
            new(9, new(0, 0, -MathF.PI / 2), Vector3.Zero),
            new(12, Vector3.Zero, new(0, 0, MathF.PI / 2)),
        ];
        var free = new PbikRig(skeleton, 0, [4, 7, 10, 13], settings, limits, .001f, 256);
        var stiff = new PbikRig(skeleton, 0, [4, 7, 10, 13],
            [.. settings, new(0, PositionStiffness: 1, RotationStiffness: 1)], limits, .001f, 256);
        var storage = new PoseStorage();
        using SkeletonInstance freeBody = storage.CreateInstance(skeleton);
        using SkeletonInstance stiffBody = storage.CreateInstance(skeleton);
        Entity freeTarget = PoseAnimation.CreateTarget(world, freeBody, resultCount: 2, output: new PoseResultId(1));
        Entity stiffTarget = PoseAnimation.CreateTarget(world, stiffBody, resultCount: 2, output: new PoseResultId(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.SolvePbik(freeTarget, new(free, new PoseResultId(0), new PoseResultId(1)));
            builder.SolvePbik(stiffTarget, new(stiff, new PoseResultId(0), new PoseResultId(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(freeTarget, new(clip, new PoseResultId(0), Loop: true));
            builder.Leaf<ClipParameters, ClipLeaf>(stiffTarget, new(clip, new PoseResultId(0), Loop: true));
        }
        Entity animation = builder.Play();
        PbikTarget[] goals =
        [
            new(new(-1.9320508f, 1.5f, 0)), new(new(2.5320508f, 1.5f, 0)),
            new(new(-.2f, -1.7320508f, 0)), new(new(.8f, -1.7320508f, 0)),
        ];
        world.ExecuteBufferWrite<PbikInstance, PbikTarget[]>(animation, ref goals, static (instances, ref goals) =>
        {
            goals.CopyTo(instances[0].Targets, 0);
            goals.CopyTo(instances[1].Targets, 0);
        });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PbikSystem()));
        var context = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref context);
        for (int effector = 0; effector < goals.Length; effector++)
        {
            Near(goals[effector].Position, freeBody.ModelPose.GetBone(free.Effectors[effector]).Transform.Translation,
                free.Tolerance + .00001f);
            Near(goals[effector].Position, stiffBody.ModelPose.GetBone(stiff.Effectors[effector]).Transform.Translation,
                stiff.Tolerance + .00001f);
        }
        float fullWeightPelvisX = freeBody.ModelPose.GetBone(0).Transform.Translation.X;
        Assert.True(fullWeightPelvisX > .1f);
        Near(Vector3.Zero, stiffBody.ModelPose.GetBone(0).Transform.Translation, .0001f);
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, stiffBody.LocalPose[0].Rotation), .0001f);
        Assert.True(MathF.Abs(AngleZ(freeBody.LocalPose[1].Rotation) - AngleZ(stiffBody.LocalPose[1].Rotation)) > .01f);

        for (int effector = 0; effector < goals.Length; effector++) goals[effector] = goals[effector] with { Weight = .25f };
        world.ExecuteBufferWrite<PbikInstance, PbikTarget[]>(animation, ref goals, static (instances, ref goals) =>
        {
            goals.CopyTo(instances[0].Targets, 0);
            goals.CopyTo(instances[1].Targets, 0);
        });
        systems.Update(ref context);
        Vector3[] weightedGoals =
        [
            new(-2.3580127f, 1.5f, 0), new(2.5080127f, 1.5f, 0),
            new(-.425f, -1.9330127f, 0), new(.575f, -1.9330127f, 0),
        ];
        for (int effector = 0; effector < goals.Length; effector++)
        {
            Near(weightedGoals[effector], freeBody.ModelPose.GetBone(free.Effectors[effector]).Transform.Translation,
                free.Tolerance + .00001f);
            Near(weightedGoals[effector], stiffBody.ModelPose.GetBone(stiff.Effectors[effector]).Transform.Translation,
                stiff.Tolerance + .00001f);
        }
        Assert.True(freeBody.ModelPose.GetBone(0).Transform.Translation.X < fullWeightPelvisX);
        Near(Vector3.Zero, stiffBody.ModelPose.GetBone(0).Transform.Translation, .0001f);

        systems.Update(ref context);
        for (int effector = 0; effector < goals.Length; effector++)
        {
            Vector3 animatedGoal = weightedGoals[effector] + new Vector3(.15f, 0, 0);
            Near(animatedGoal, freeBody.ModelPose.GetBone(free.Effectors[effector]).Transform.Translation,
                free.Tolerance + .00001f);
            Near(animatedGoal, stiffBody.ModelPose.GetBone(stiff.Effectors[effector]).Transform.Translation,
                stiff.Tolerance + .00001f);
        }
        Near(new(.2f, 0, 0), stiffBody.ModelPose.GetBone(0).Transform.Translation, .0001f);
        Assert.Equal(.75, freeBody.ModelPose.SampleTime, 5);
        Assert.Equal(.75, stiffBody.ModelPose.SampleTime, 5);
    }

    private static Skeleton CreateBody()
    {
        Vector3[] translations =
        [
            Vector3.Zero, new(0, 1, 0), new(-.5f, .5f, 0), new(-1, 0, 0), new(-1, 0, 0),
            new(.5f, .5f, 0), new(1, 0, 0), new(1, 0, 0), new(-.5f, 0, 0), new(0, -1, 0),
            new(0, -1, 0), new(.5f, 0, 0), new(0, -1, 0), new(0, -1, 0), new(0, .7f, 0),
        ];
        BoneTransform[] pose = new BoneTransform[translations.Length];
        for (int bone = 0; bone < pose.Length; bone++)
            pose[bone] = new() { Translation = translations[bone], Rotation = Quaternion.Identity, Scale = Vector3.One };
        return new Skeleton([-1, 0, 1, 2, 3, 1, 5, 6, 0, 8, 9, 0, 11, 12, 1], pose,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
    }

    private static float AngleZ(Quaternion rotation)
    {
        Vector3 direction = Vector3.Transform(Vector3.UnitX, rotation);
        return MathF.Atan2(direction.Y, direction.X);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, tolerance);
}
