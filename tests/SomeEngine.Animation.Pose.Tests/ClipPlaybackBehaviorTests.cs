using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class ClipPlaybackBehaviorTests
{
    [Fact]
    public void SharedGraphInstancesPublishTheirOwnTimeAndFinalPoseBeforeCompletion()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        Entity firstTarget = PoseAnimation.CreateTarget(world, first);
        Entity secondTarget = PoseAnimation.CreateTarget(world, second);
        AnimationClip clip = CreateClip(skeleton);
        using var builder = new AnimationBuilder(world);
        builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new PoseResultId(0)));
        using CompiledGraph graph = builder.Compile();
        Entity firstAnimation = graph.Play(world, firstTarget);
        Entity secondAnimation = graph.Play(world, secondTarget);
        PropertyAnimation.SetSpeed(world, secondAnimation, 0.5f);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);

        Tick(systems, world, 0.5);
        Assert.Equal(new Vector3(1, 1, 0), first.ModelPose.Transforms[1].Translation);
        Assert.Equal(new Vector3(0.5f, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.5, first.ModelPose.Time);
        Assert.Equal(0.25, second.ModelPose.Time);
        Tick(systems, world, 0.5);
        Assert.Equal(new Vector3(2, 1, 0), first.ModelPose.Transforms[1].Translation);
        Assert.Equal(new Vector3(1, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(1u, world.Read<Timeline>(firstAnimation).Completions);
        Assert.True(world.IsEnabled<Timeline>(secondAnimation));
        Assert.Equal(1, first.ModelPose.Time);
    }

    [Fact]
    public void LoopEventsAndExtractedMotionProduceTheExpectedMovingWorldPose()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance);
        PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(0));
        using var builder = new AnimationBuilder(world);
        builder.Leaf<ClipParameters, ClipLeaf>(target,
            new(CreateClip(skeleton, turn: true), new PoseResultId(0), Loop: true, RootMotion: RootMotionMode.Extract));
        Entity animation = builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);
        Matrix4x4 actor = Matrix4x4.Identity;

        Tick(systems, world, 1);
        actor = PoseAnimation.ReadResult(world, in output).RootMotion.Transform * actor;
        Near(new Vector3(2, 0, 0), actor.Translation);
        Tick(systems, world, 1.25);
        PoseResult result = PoseAnimation.ReadResult(world, in output);
        actor = result.RootMotion.Transform * actor;

        Near(new Vector3(1.5f, 2, 0), actor.Translation);
        Near(new Vector3(0, 1, 0), instance.ModelPose.Transforms[1].Translation);
        Near(new Vector3(1.8826834f, 1.0761205f, 0),
            Vector3.Transform(instance.ModelPose.Transforms[1].Translation, actor));
        Assert.Equal(2.25, instance.ModelPose.Time);
        Assert.Equal(3u, EventCount(world, animation, 7));
        Assert.Equal(2u, EventCount(world, animation, 9));
        Assert.True(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void ConsumerLeafUsesTheProducersFinalSampleAcrossAStageBoundary()
    {
        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(CreateSkeleton());
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new PoseResultId(1));
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.Leaf<PoseSourceParameters, PoseSourceLeaf>(target, new(new PoseResultId(0), 1));
            builder.Boundary(StageEnd.Duration);
            builder.Leaf<PoseOffsetParameters, PoseOffsetLeaf>(target, new(new PoseResultId(0), new PoseResultId(1), 0.5f));
        }
        Entity animation = builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
        {
            AnimationSystems.Add(composition, new PoseSourceSystem(), AnimationPhase.Composition);
            AnimationSystems.Add(composition, new PoseOffsetSystem(), AnimationPhase.Composition);
        });

        Tick(systems, world, 0.75);
        Tick(systems, world, 0.5);
        Assert.Equal(new Vector3(3, 2, 0), instance.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.25, instance.ModelPose.Time);
        Assert.True(world.IsEnabled<Timeline>(animation));
        Tick(systems, world, 0.25);
        Assert.Equal(new Vector3(3, 3, 0), instance.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.5, instance.ModelPose.Time);
        Assert.Equal(1u, world.Read<Timeline>(animation).Completions);
    }

    private static Skeleton CreateSkeleton() => new([-1, 0],
        [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
         new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }],
        [1, 2]);

    private static AnimationClip CreateClip(Skeleton skeleton, bool turn = false) => new(skeleton, 1,
        ClipSampler.Create<WalkingData, WalkingSampler>(new WalkingData(turn)),
        [new ClipEvent(0.25f, 7), new ClipEvent(0.75f, 9)], rootMotionBone: 0);

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

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    public sealed class WalkingData(bool turn)
    {
        public bool Turn { get; } = turn;
    }
    public readonly struct WalkingSampler : IClipSampler<WalkingData>
    {
        public static void SampleLocal(WalkingData data, float time, Span<BoneTransform> destination)
        {
            destination[0].Translation = new Vector3(2 * time, 0, 0);
            if (data.Turn) destination[0].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f * time);
        }
    }
}

public readonly record struct PoseSourceParameters(PoseResultId Output, float Duration);
public readonly record struct PoseOffsetParameters(PoseResultId Input, PoseResultId Output, float Duration);

[BufferCapacity(1)]
public struct PoseSourceInstance : IBufferElement
{
    public AnimationLeafBatch<PoseSourceParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    public uint Starts;
    public float Time;
}

[BufferCapacity(1)]
public struct PoseOffsetInstance : IBufferElement
{
    public AnimationLeafBatch<PoseOffsetParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    public PoseResultReference Input;
    public PoseResultReference Output;
}

public readonly struct PoseSourceLeaf : IAnimationLeaf<PoseSourceParameters>
{
    public static MotionBoundary Describe(in PoseSourceParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PoseSourceParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PoseSourceInstance>(animation)) world.AddBuffer<PoseSourceInstance>(animation);
        var source = new PoseSourceInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
        };
        world.ExecuteBufferWrite<PoseSourceInstance, PoseSourceInstance>(animation, ref source,
            static (sources, ref source) => sources.Add(source));
    }
    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<PoseSourceInstance>(animation, static sources => sources.Clear());
}

public readonly struct PoseOffsetLeaf : IAnimationLeaf<PoseOffsetParameters>
{
    public static MotionBoundary Describe(in PoseOffsetParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PoseOffsetParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PoseOffsetInstance>(animation)) world.AddBuffer<PoseOffsetInstance>(animation);
        var offset = new PoseOffsetInstance
        {
            Definition = definitions, Index = index, Placement = placement, Target = target,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
        };
        world.ExecuteBufferWrite<PoseOffsetInstance, PoseOffsetInstance>(animation, ref offset,
            static (offsets, ref offset) => offsets.Add(offset));
    }
    public static void Complete(World world, Entity animation) =>
        world.ExecuteBufferWrite<PoseOffsetInstance, World>(animation, ref world, static (offsets, ref world) =>
        {
            foreach (ref PoseOffsetInstance offset in offsets.AsSpan())
                offset.Input = PoseAnimation.Resolve(world, offset.Target, offset.Definition.Parameters[offset.Index].Input);
        });
    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<PoseOffsetInstance>(animation, static offsets => offsets.Clear());
}

public struct PoseSourceSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _sources;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _sources = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<PoseSourceInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_sources, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref PoseSourceInstance source in row.Buffer<PoseSourceInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in source.Placement);
                    if (!time.Active) continue;
                    if (source.Starts != time.Starts)
                    {
                        source.Starts = time.Starts;
                        source.Time = 0;
                    }
                    source.Time += time.Time.Delta;
                    float duration = source.Definition.Parameters[source.Index].Duration;
                    float elapsed = Math.Min(time.Time.Time, duration);
                    var value = (Start: source.Output.Range.Start, X: 3 * source.Time);
                    world.ExecuteBufferWrite<LocalPose, (int Start, float X)>(source.Output.Entity, ref value,
                        static (values, ref value) => values[value.Start].Transform.Translation = new(value.X, 0, 0));
                    PoseNodeResults.Write(world, in source.Output, elapsed, in time);
                    AnimationGraph.Submit(world, row.Entity, in source.Placement, elapsed < duration);
                }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_sources);
}

public struct PoseOffsetSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _offsets;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _offsets = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().ReadBuffer<PoseOffsetInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_offsets, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (PoseOffsetInstance offset in row.ReadBuffer<PoseOffsetInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in offset.Placement);
                    if (!time.Active) continue;
                    float duration = offset.Definition.Parameters[offset.Index].Duration;
                    float elapsed = Math.Min(time.Time.Time, duration);
                    var copy = (Input: offset.Input.Range, Output: offset.Output.Range, Y: 4 * elapsed);
                    world.ExecuteBufferWrite<LocalPose, (PoseRange Input, PoseRange Output, float Y)>(offset.Output.Entity, ref copy,
                        static (values, ref copy) =>
                        {
                            Span<LocalPose> poses = values.AsSpan();
                            poses.Slice(copy.Input.Start, copy.Input.Count).CopyTo(poses.Slice(copy.Output.Start, copy.Output.Count));
                            poses[copy.Output.Start].Transform.Translation.Y += copy.Y;
                        });
                    PoseNodeResults.Write(world, in offset.Output, elapsed, in time);
                    AnimationGraph.Submit(world, row.Entity, in offset.Placement, elapsed < duration);
                }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_offsets);
}

internal static class PoseNodeResults
{
    internal static void Write(World world, in PoseResultReference output, float elapsed, in AnimationLeafTime time)
    {
        var value = (Index: output.Index, Time: elapsed, Weight: time.Weight, Starts: time.Starts);
        world.ExecuteBufferWrite<PoseResult, (int Index, float Time, float Weight, uint Starts)>(output.Entity, ref value,
            static (results, ref value) =>
            {
                ref PoseResult result = ref results[value.Index];
                result.Time = value.Time;
                result.Weight = value.Weight;
                result.Starts = value.Starts;
                result.Version++;
            });
    }
}
