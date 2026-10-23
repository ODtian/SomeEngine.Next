using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class FabrikBehaviorTests
{
    [Fact]
    public void SharedFabrikGraphReachesEachTargetWhileKeepingBoneLengthsAndTheSelectedJointLimit()
    {
        using var world = new World();
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0],
        [
            new() { Translation = new(3, 4, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Scale = Vector3.One },
            new() { Translation = Vector3.UnitX, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Scale = Vector3.One },
            new() { Translation = Vector3.UnitX, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -2 * MathF.PI / 9), Scale = Vector3.One },
            new() { Translation = Vector3.UnitX, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 9), Scale = Vector3.One },
            new() { Translation = Vector3.UnitX, Rotation = Quaternion.Identity, Scale = Vector3.One },
            new() { Translation = Vector3.UnitZ, Rotation = Quaternion.Identity, Scale = Vector3.One },
        ], [1, 2, 3, 4, 5, 6]);
        var clip = new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new RawClipData(1, [])));
        var chain = new FabrikChain(skeleton, [1, 2, 3, 4],
            [new FabrikJointLimit(1, Vector3.UnitX, MathF.PI / 6)], tolerance: .0001f, iterations: 64);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        Entity[] targets =
        [
            PoseAnimation.CreateTarget(world, first, resultCount: 2, output: new PoseResultId(1)),
            PoseAnimation.CreateTarget(world, second, resultCount: 2, output: new PoseResultId(1)),
        ];
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.SolveFabrik(Entity.Null, new(chain, new PoseResultId(0), new PoseResultId(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new PoseResultId(0), Loop: true));
        }
        using CompiledGraph graph = builder.Compile();
        Entity[] animations = [graph.Play(world, targets[0]), graph.Play(world, targets[1])];
        Vector3[] goals = [new(1, 5, 0), new(5, 5, 0)];
        for (int index = 0; index < animations.Length; index++)
            world.ExecuteBufferWrite<FabrikInstance, Vector3>(animations[index], ref goals[index],
                static (instances, ref goal) => instances[0].Target = goal);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new FabrikSystem()));
        var context = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref context);
        SkeletonInstance[] instances = [first, second];
        for (int index = 0; index < instances.Length; index++)
        {
            ModelPose pose = instances[index].ModelPose;
            Near(goals[index], pose.GetBone(4).Transform.Translation, chain.Tolerance + .00001f);
            Near(new(3, 5, 0), pose.GetBone(1).Transform.Translation, .0001f);
            Near(new(3, 4, 1), pose.GetBone(5).Transform.Translation, .0001f);
            for (int bone = 1; bone < 4; bone++)
                Assert.InRange(Vector3.Distance(pose.GetBone(bone).Transform.Translation,
                    pose.GetBone(bone + 1).Transform.Translation), .9999f, 1.0001f);
            Vector3 firstSegment = Vector3.Normalize(pose.GetBone(2).Transform.Translation
                - pose.GetBone(1).Transform.Translation);
            // The parent's +X axis is model +Y; the initial chain points along model -X.
            float swing = MathF.Acos(Math.Clamp(Vector3.Dot(firstSegment, Vector3.UnitY), -1, 1));
            Assert.InRange(swing, 0, MathF.PI / 6 + .0001f);
            Assert.Equal(.25, pose.SampleTime, 5);
        }

        goals[0] = new(1.5f, 5.25f, 0);
        world.ExecuteBufferWrite<FabrikInstance, Vector3>(animations[0], ref goals[0],
            static (instances, ref goal) => instances[0].Target = goal);
        systems.Update(ref context);
        Near(goals[0], first.ModelPose.GetBone(4).Transform.Translation, chain.Tolerance + .00001f);
        Near(goals[1], second.ModelPose.GetBone(4).Transform.Translation, chain.Tolerance + .00001f);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, tolerance);
}
