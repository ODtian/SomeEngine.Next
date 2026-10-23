using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PoseStateMachineBehaviorTests
{
    [Fact]
    public void ParameterTransitionKeepsPoseTimeEventsAndMotionInTheSameFrame()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 3, output: new PoseResultId(2));
        AnimationClip walk = CreateClip(skeleton, new(2, 1, 1), [new(0.25f, 11)]);
        AnimationClip run = CreateClip(skeleton, new(6, 5, 2), [new(0.25f, 21), new(0.5f, 22)]);
        var inputs = new GraphParameters();
        var blend = new PoseBlend([new(new PoseResultId(0)), new(new PoseResultId(1))]);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            using (var machine = builder.StateMachine(ref inputs))
            {
                using (machine.State(0, initial: true))
                    builder.Leaf<ClipParameters, ClipLeaf>(target,
                        new(walk, new PoseResultId(0), Loop: true, RootMotion: RootMotionMode.Extract));
                using (machine.State(1))
                    builder.Leaf<ClipParameters, ClipLeaf>(target,
                        new(run, new PoseResultId(1), Loop: true, RootMotion: RootMotionMode.Extract));
                machine.Transition(0, 1, GraphCondition.Equal(0, 1), crossfade: 0.5f);
            }
            builder.BlendPose(target, new(blend, new PoseResultId(2), new(StageEnd.Settled)));
        }
        Entity animation = builder.Play();
        PoseResultReference walking = PoseAnimation.Resolve(world, target, new PoseResultId(0));
        PoseResultReference running = PoseAnimation.Resolve(world, target, new PoseResultId(1));
        PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(2));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));
        Matrix4x4 actor = Matrix4x4.Identity;

        Tick(systems, world, 0.25);
        actor = PoseAnimation.ReadResult(world, in output).RootMotion.Transform * actor;
        Near(new Vector3(0.5f, 1.25f, 0), Vector3.Transform(instance.ModelPose.Transforms[1].Translation, actor));
        Assert.Equal(1u, EventCount(world, animation, 11));

        inputs.Values[0] = 1;
        world.Replace(animation, inputs);
        Tick(systems, world, 0.25);
        PoseResult transition = PoseAnimation.ReadResult(world, in output);
        actor = transition.RootMotion.Transform * actor;
        Assert.Equal(0.5f, PoseAnimation.ReadResult(world, in walking).Weight);
        Assert.Equal(0.5f, PoseAnimation.ReadResult(world, in running).Weight);
        Assert.Equal(0.375, transition.Time);
        Assert.Equal(0.1875, transition.Phase);
        Near(new Vector3(1, 0, 0), transition.RootMotion.Translation);
        Near(new Vector3(1.5f, 3.5f, 0), Vector3.Transform(instance.ModelPose.Transforms[1].Translation, actor));
        Assert.Equal(1u, EventCount(world, animation, 21));

        Tick(systems, world, 0.25);
        PoseResult runningResult = PoseAnimation.ReadResult(world, in output);
        actor = runningResult.RootMotion.Transform * actor;
        Assert.Equal(0f, PoseAnimation.ReadResult(world, in walking).Weight);
        Assert.Equal(1f, PoseAnimation.ReadResult(world, in running).Weight);
        Assert.Equal(0.5, runningResult.Time);
        Assert.Equal(0.25, runningResult.Phase);
        Near(new Vector3(3, 6, 0), Vector3.Transform(instance.ModelPose.Transforms[1].Translation, actor));
        Assert.Equal(1u, EventCount(world, animation, 11));
        Assert.Equal(1u, EventCount(world, animation, 22));
    }

    [Fact]
    public void SharedGraphsKeepUserConditionHistoryAndInterruptedFadesPerCharacter()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        Entity firstTarget = PoseAnimation.CreateTarget(world, first, resultCount: 4, output: new PoseResultId(3));
        Entity secondTarget = PoseAnimation.CreateTarget(world, second, resultCount: 4, output: new PoseResultId(3));
        AnimationClip idle = CreateClip(skeleton, new(0, 0, 0));
        AnimationClip walk = CreateClip(skeleton, new(0, 10, 0));
        AnimationClip jump = CreateClip(skeleton, new(0, 20, 0));
        var inputs = new GraphParameters();
        var blend = new PoseBlend([new(new PoseResultId(0)), new(new PoseResultId(1)), new(new PoseResultId(2))]);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            using (var machine = builder.StateMachine(ref inputs))
            {
                using (machine.State(0, initial: true))
                    builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(idle, new PoseResultId(0), Loop: true));
                using (machine.State(1))
                    builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(walk, new PoseResultId(1), Loop: true));
                using (machine.State(2))
                    builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(jump, new PoseResultId(2), Loop: true));
                machine.Transition(0, 1,
                    GraphCondition.Create<HeldInputParameters, HeldInputState, HeldInputCondition>(new(0, 1, 0.5f)), crossfade: 0.5f);
                machine.Transition(1, 2, GraphCondition.Equal(2, 1), crossfade: 0.5f);
            }
            builder.BlendPose(Entity.Null, new(blend, new PoseResultId(3), new(StageEnd.Settled)));
        }
        using CompiledGraph graph = builder.Compile();
        Entity firstAnimation = graph.Play(world, firstTarget);
        Entity secondAnimation = graph.Play(world, secondTarget);
        GraphParameters firstInputs = world.Read<GraphParameters>(firstAnimation);
        GraphParameters secondInputs = world.Read<GraphParameters>(secondAnimation);
        firstInputs.Values[0] = 1;
        firstInputs.Values[1] = secondInputs.Values[1] = 0.25f;
        world.Replace(firstAnimation, firstInputs);
        world.Replace(secondAnimation, secondInputs);
        PoseResultReference firstOutput = PoseAnimation.Resolve(world, firstTarget, new PoseResultId(3));
        PoseResultReference secondOutput = PoseAnimation.Resolve(world, secondTarget, new PoseResultId(3));
        PoseResultReference firstIdle = PoseAnimation.Resolve(world, firstTarget, new PoseResultId(0));
        PoseResultReference firstWalk = PoseAnimation.Resolve(world, firstTarget, new PoseResultId(1));
        PoseResultReference firstJump = PoseAnimation.Resolve(world, firstTarget, new PoseResultId(2));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));

        Tick(systems, world, 0.25);
        Assert.Equal(0, first.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(0, second.ModelPose.Transforms[1].Translation.Y);
        Tick(systems, world, 0.25);
        Assert.Equal(5, first.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(0, second.ModelPose.Transforms[1].Translation.Y);

        firstInputs.Values[2] = 1;
        secondInputs.Values[0] = 1;
        world.Replace(firstAnimation, firstInputs);
        world.Replace(secondAnimation, secondInputs);
        Tick(systems, world, 0.25);
        Assert.Equal(12.5f, first.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(0, second.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(0.25f, PoseAnimation.ReadResult(world, in firstIdle).Weight);
        Assert.Equal(0.25f, PoseAnimation.ReadResult(world, in firstWalk).Weight);
        Assert.Equal(0.5f, PoseAnimation.ReadResult(world, in firstJump).Weight);

        Tick(systems, world, 0.25);
        Assert.Equal(20, first.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(5, second.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(0.5, PoseAnimation.ReadResult(world, in firstOutput).Time);
        Assert.Equal(0.625, PoseAnimation.ReadResult(world, in secondOutput).Time);
        Tick(systems, world, 0.25);
        Assert.Equal(20, first.ModelPose.Transforms[1].Translation.Y);
        Assert.Equal(10, second.ModelPose.Transforms[1].Translation.Y);
    }

    private static Skeleton CreateSkeleton() => new([-1, 0],
        [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
         new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);

    private static AnimationClip CreateClip(Skeleton skeleton, ClipMotion motion, ImmutableArray<ClipEvent> events = default) =>
        new(skeleton, 2, ClipSampler.Create<ClipMotion, MotionSampler>(motion), events, rootMotionBone: 0);

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static uint EventCount(World world, Entity animation, ushort id)
    {
        var count = (Id: id, Value: 0u);
        world.ExecuteBufferRead<TimelineEvent, (ushort Id, uint Value)>(animation, ref count,
            static (events, ref count) =>
            {
                foreach (TimelineEvent marker in events.AsSpan())
                    if (marker.Id == count.Id) count.Value = marker.Count;
            });
        return count.Value;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private sealed record ClipMotion(float Speed, float Height, float HeightRate);

    private readonly record struct HeldInputParameters(byte Input, byte Delta, float Duration);
    private struct HeldInputState { public float Elapsed; }
    private readonly struct HeldInputCondition : IAnimationCondition<HeldInputParameters, HeldInputState>
    {
        public static bool Matches(in HeldInputParameters parameters, in GraphParameters inputs, ref HeldInputState state)
        {
            if (inputs.Values[parameters.Input] == 0)
            {
                state.Elapsed = 0;
                return false;
            }
            state.Elapsed += inputs.Values[parameters.Delta];
            if (state.Elapsed < parameters.Duration) return false;
            state.Elapsed = 0;
            return true;
        }
    }

    private readonly struct MotionSampler : IClipSampler<ClipMotion>
    {
        public static void SampleLocal(ClipMotion data, float time, Span<BoneTransform> destination)
        {
            destination[0].Translation.X = data.Speed * time;
            destination[1].Translation.Y = data.Height + data.HeightRate * time;
        }
    }
}
