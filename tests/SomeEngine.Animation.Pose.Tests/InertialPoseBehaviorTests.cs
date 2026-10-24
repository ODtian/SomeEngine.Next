using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class InertialPoseBehaviorTests
{
    [Fact]
    public void MovingActionSwitchesPreserveEachCharactersPoseAndVelocityBeforeConvergingToItsSelectedAction()
    {
        var skeleton = new Skeleton([-1, 0],
        [
            new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
            new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
        ], [1, 2]);
        var actionA = new MovingAction(new(1, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f), Vector3.One,
            new(new(2, .5f, -.2f), new(0, 0, .6f), new(.1f, .02f, 0)));
        var actionB = new MovingAction(new(-4, 1, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), new(1.3f, .8f, 1.1f),
            new(new(-1, .4f, .1f), new(.5f, 0, 0), new(-.04f, .03f, 0)));
        var actionC = new MovingAction(new(3, -2, -1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.8f), new(.7f, 1.2f, .9f),
            new(new(.3f, -.8f, .6f), new(0, .7f, .2f), new(.06f, -.02f, .01f)));
        AnimationClip clipA = Clip(skeleton, actionA), clipB = Clip(skeleton, actionB), clipC = Clip(skeleton, actionC);
        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton), second = storage.CreateInstance(skeleton);
        Entity firstTarget = PoseAnimation.CreateTarget(world, first, resultCount: 3, output: new(2));
        Entity secondTarget = PoseAnimation.CreateTarget(world, second, resultCount: 3, output: new(2));
        var controls = new GraphParameters();
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(1))]), new(2), new(StageEnd.Settled)));
            builder.InertializePose(Entity.Null, new(new(0), new(1), DecayRate: 4));
            using var machine = builder.StateMachine(ref controls);
            using (machine.State(0, initial: true)) builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clipA, new(0), Loop: true));
            using (machine.State(1)) builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clipB, new(0), Loop: true));
            using (machine.State(2)) builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clipC, new(0), Loop: true));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1));
            machine.Transition(1, 2, GraphCondition.Equal(0, 2));
        }
        using CompiledGraph graph = builder.Compile();
        Entity firstAnimation = graph.Play(world, firstTarget), secondAnimation = graph.Play(world, secondTarget);
        SelectAction(world, firstAnimation, 0, actionA.Velocity);
        SelectAction(world, secondAnimation, 0, actionA.Velocity);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new PoseBlendSystem());
            nodes.Add(new InertialPoseSystem());
        });

        Tick(systems, world, .5);
        Near(Sample(clipA, .5f), first.LocalPose[1]);
        BoneTransform beforeSwitch = first.LocalPose[1];
        SelectAction(world, firstAnimation, 1, actionB.Velocity);
        Tick(systems, world, 0);
        Near(beforeSwitch, first.LocalPose[1]);
        Near(actionA.Velocity, Velocity(world, firstAnimation));
        const double step = .0001;
        Tick(systems, world, step);
        Assert.True(world.IsEnabled<Timeline>(firstAnimation));
        Assert.Equal(.5001, first.ModelPose.Time, 5);
        Assert.Equal(.0001, first.ModelPose.SampleTime, 5);
        ContinuousMotion(beforeSwitch, first.LocalPose[1], actionA.Velocity, (float)step);
        Near(Sample(clipA, .5001f), second.LocalPose[1]);

        Tick(systems, world, .2);
        BoneTransform beforeMotion = first.LocalPose[1];
        BoneVelocity beforeVelocity = Velocity(world, firstAnimation);
        Tick(systems, world, step);
        ContinuousMotion(beforeMotion, first.LocalPose[1], beforeVelocity, (float)step);
        BoneTransform firstBeforeRepeat = first.LocalPose[1], secondBeforeRepeat = second.LocalPose[1];
        BoneVelocity firstVelocity = Velocity(world, firstAnimation);
        SelectAction(world, firstAnimation, 2, actionC.Velocity);
        SelectAction(world, secondAnimation, 1, actionB.Velocity);
        Tick(systems, world, step);
        ContinuousMotion(firstBeforeRepeat, first.LocalPose[1], firstVelocity, (float)step);
        ContinuousMotion(secondBeforeRepeat, second.LocalPose[1], actionA.Velocity, (float)step);
        Near(firstVelocity, Velocity(world, firstAnimation));
        Near(actionA.Velocity, Velocity(world, secondAnimation));

        Tick(systems, world, 5);
        PoseResultReference firstSource = PoseAnimation.Resolve(world, firstTarget, new(0));
        PoseResultReference secondSource = PoseAnimation.Resolve(world, secondTarget, new(0));
        Near(Sample(clipC, (float)PoseAnimation.ReadResult(world, firstSource).Time), first.LocalPose[1], .0001f);
        Near(Sample(clipB, (float)PoseAnimation.ReadResult(world, secondSource).Time), second.LocalPose[1], .0001f);
        Near(actionC.Velocity, Velocity(world, firstAnimation), .0001f);
        Near(actionB.Velocity, Velocity(world, secondAnimation), .0001f);
    }

    private static AnimationClip Clip(Skeleton skeleton, MovingAction action) => new(skeleton, 16,
        ClipSampler.Create<MovingAction, MovingSampler>(action));

    private static BoneTransform Sample(AnimationClip clip, float time)
    {
        var pose = new BoneTransform[2];
        clip.SampleLocal(time, pose);
        return pose[1];
    }

    private static void SelectAction(World world, Entity animation, ulong action, BoneVelocity velocity)
    {
        GraphParameters controls = world.Read<GraphParameters>(animation);
        controls.Values[0] = action;
        world.Replace(animation, controls);
        var change = (Action: action, Velocity: velocity);
        world.ExecuteBufferWrite<InertialPoseInstance, (ulong Action, BoneVelocity Velocity)>(animation, ref change,
            static (instances, ref change) =>
            {
                instances[0].Action = change.Action;
                instances[0].SourceVelocities[1] = change.Velocity;
            });
    }

    private static BoneVelocity Velocity(World world, Entity animation)
    {
        BoneVelocity velocity = default;
        world.ExecuteBufferRead<InertialPoseInstance, BoneVelocity>(animation, ref velocity,
            static (instances, ref velocity) => velocity = instances[0].Velocities[1]);
        return velocity;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static void ContinuousMotion(BoneTransform before, BoneTransform after, BoneVelocity velocity, float seconds)
    {
        Near(velocity.Translation, (after.Translation - before.Translation) / seconds, .025f);
        foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY })
        {
            Vector3 direction = Vector3.Transform(axis, before.Rotation);
            Near(Vector3.Cross(velocity.Angular, direction),
                (Vector3.Transform(axis, after.Rotation) - direction) / seconds, .006f);
        }
        Vector3 ratio = after.Scale / before.Scale;
        Near(velocity.LogScale, new Vector3(MathF.Log(ratio.X), MathF.Log(ratio.Y), MathF.Log(ratio.Z)) / seconds, .004f);
    }

    private static void Near(BoneTransform expected, BoneTransform actual, float tolerance = .00001f)
    {
        Near(expected.Translation, actual.Translation, tolerance);
        Near(Vector3.Transform(Vector3.UnitX, expected.Rotation), Vector3.Transform(Vector3.UnitX, actual.Rotation), tolerance);
        Near(Vector3.Transform(Vector3.UnitY, expected.Rotation), Vector3.Transform(Vector3.UnitY, actual.Rotation), tolerance);
        Near(expected.Scale, actual.Scale, tolerance);
    }

    private static void Near(BoneVelocity expected, BoneVelocity actual, float tolerance = .00001f)
    {
        Near(expected.Translation, actual.Translation, tolerance);
        Near(expected.Angular, actual.Angular, tolerance);
        Near(expected.LogScale, actual.LogScale, tolerance);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00001f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}; actual {actual}; tolerance {tolerance}.");

    private sealed record MovingAction(Vector3 Position, Quaternion Rotation, Vector3 Scale, BoneVelocity Velocity);

    private readonly struct MovingSampler : IClipSampler<MovingAction>
    {
        public static void SampleLocal(MovingAction action, float time, Span<BoneTransform> destination)
        {
            Vector3 angular = action.Velocity.Angular;
            float speed = angular.Length();
            Vector3 scale = action.Velocity.LogScale * time;
            destination[1] = new()
            {
                Translation = action.Position + action.Velocity.Translation * time,
                Rotation = Quaternion.CreateFromAxisAngle(angular / speed, speed * time) * action.Rotation,
                Scale = action.Scale * new Vector3(MathF.Exp(scale.X), MathF.Exp(scale.Y), MathF.Exp(scale.Z)),
            };
        }
    }
}
