using System.Numerics;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
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
        Assert.Equal(0.25, second.ModelPose.SampleTime);
        Assert.Equal(0.5, second.ModelPose.Time);
        Tick(systems, world, 0.5);
        Assert.Equal(new Vector3(2, 1, 0), first.ModelPose.Transforms[1].Translation);
        Assert.Equal(new Vector3(1, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(1u, world.ReadComponentData<Timeline>(firstAnimation).Completions);
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
        Assert.Equal(0.25, instance.ModelPose.SampleTime);
        Assert.Equal(1.25, instance.ModelPose.Time);
        Assert.True(world.IsEnabled<Timeline>(animation));
        Tick(systems, world, 0.25);
        Assert.Equal(new Vector3(3, 3, 0), instance.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.5, instance.ModelPose.SampleTime);
        Assert.Equal(1.5, instance.ModelPose.Time);
        Assert.Equal(1u, world.ReadComponentData<Timeline>(animation).Completions);
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
        {
            using var events = world.ReadBuffer<TimelineEvent>(animation);
            Visit(events, ref count);

            static void Visit(ReadOnlyBuffer<TimelineEvent> events, ref (ushort Id, uint Value) count)
            {
                foreach (TimelineEvent marker in events.AsSpan())
                    if (marker.Id == count.Id) count.Value = marker.Count;

            }
        }
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

public struct PoseSourceInstance : IBufferElement
{
    public AnimationLeafBatch<PoseSourceParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    public uint Starts;
    public float Time;
}

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
        if (!world.Has<Buffer<PoseSourceInstance>>(animation)) world.AddBuffer<PoseSourceInstance>(animation);
        var source = new PoseSourceInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
        };
        {
            using var sources = world.GetBuffer<PoseSourceInstance>(animation);
            Visit(sources, ref source);

            static void Visit(DynamicBuffer<PoseSourceInstance> sources, ref PoseSourceInstance source)
            {
                sources.Add(source);

            }
        }
    }
    public static void Release(World world, Entity animation)
    {
        using var sources = world.GetBuffer<PoseSourceInstance>(animation);
        sources.Clear();

    }
}

public readonly struct PoseOffsetLeaf : IAnimationLeaf<PoseOffsetParameters>
{
    public static MotionBoundary Describe(in PoseOffsetParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PoseOffsetParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.Has<Buffer<PoseOffsetInstance>>(animation)) world.AddBuffer<PoseOffsetInstance>(animation);
        var offset = new PoseOffsetInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Target = target,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
        };
        {
            using var offsets = world.GetBuffer<PoseOffsetInstance>(animation);
            Visit(offsets, ref offset);

            static void Visit(DynamicBuffer<PoseOffsetInstance> offsets, ref PoseOffsetInstance offset)
            {
                offsets.Add(offset);

            }
        }
    }
    public static void Complete(World world, Entity animation)
    {
        using var offsets = world.GetBuffer<PoseOffsetInstance>(animation);
        Visit(offsets, ref world);

        static void Visit(DynamicBuffer<PoseOffsetInstance> offsets, ref World world)
        {
            foreach (ref PoseOffsetInstance offset in offsets.AsSpan())
                offset.Input = PoseAnimation.Resolve(world, offset.Target, offset.Definition.Parameters[offset.Index].Input);

        }
    }
    public static void Release(World world, Entity animation)
    {
        using var offsets = world.GetBuffer<PoseOffsetInstance>(animation);
        offsets.Clear();

    }
}

public struct PoseSourceSystem : ISystem<ImmediateSystemContext>
{
    private Query _sources;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _sources = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<PoseSourceInstance>>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        using QueryScope rows = _sources.Open();
        foreach (QueryChunk chunk in rows)
        {
            var sources = chunk.GetBuffers<PoseSourceInstance>();

            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                foreach (ref PoseSourceInstance source in sources[rowIndex].AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, chunk.Entities[rowIndex], in source.Placement);
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
                    {
                        using var values = world.GetBuffer<LocalPose>(source.Output.Entity);
                        Visit(values, ref value);

                        static void Visit(DynamicBuffer<LocalPose> values, ref (int Start, float X) value)
                        {
                            values[value.Start].Transform.Translation = new(value.X, 0, 0);

                        }
                    }
                    PoseNodeResults.Write(world, in source.Output, elapsed, in time);
                    AnimationGraph.Submit(world, chunk.Entities[rowIndex], in source.Placement, elapsed < duration);
                }

        }
    }
    public void OnDestroy(ref ImmediateSystemContext context) { }

}

public struct PoseOffsetSystem : ISystem<ImmediateSystemContext>
{
    private Query _offsets;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _offsets = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<PoseOffsetInstance>>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        using QueryScope rows = _offsets.Open();
        foreach (QueryChunk chunk in rows)
        {


            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                foreach (PoseOffsetInstance offset in chunk.ReadBuffer<PoseOffsetInstance>(rowIndex).AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, chunk.Entities[rowIndex], in offset.Placement);
                    if (!time.Active) continue;
                    float duration = offset.Definition.Parameters[offset.Index].Duration;
                    float elapsed = Math.Min(time.Time.Time, duration);
                    var copy = (Input: offset.Input.Range, Output: offset.Output.Range, Y: 4 * elapsed);
                    {
                        using var values = world.GetBuffer<LocalPose>(offset.Output.Entity);
                        Visit(values, ref copy);

                        static void Visit(DynamicBuffer<LocalPose> values, ref (PoseRange Input, PoseRange Output, float Y) copy)
                        {
                            Span<LocalPose> poses = values.AsSpan();
                            poses.Slice(copy.Input.Start, copy.Input.Count).CopyTo(poses.Slice(copy.Output.Start, copy.Output.Count));
                            poses[copy.Output.Start].Transform.Translation.Y += copy.Y;

                        }
                    }
                    PoseNodeResults.Write(world, in offset.Output, elapsed, in time);
                    AnimationGraph.Submit(world, chunk.Entities[rowIndex], in offset.Placement, elapsed < duration);
                }

        }
    }
    public void OnDestroy(ref ImmediateSystemContext context) { }

}

internal static class PoseNodeResults
{
    internal static void Write(World world, in PoseResultReference output, float elapsed, in AnimationLeafTime time)
    {
        var value = (Index: output.Index, Time: elapsed, Weight: time.Weight, Starts: time.Starts);
        {
            using var results = world.GetBuffer<PoseResult>(output.Entity);
            Visit(results, ref value);

            static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, float Time, float Weight, uint Starts) value)
            {
                ref PoseResult result = ref results[value.Index];
                result.Time = value.Time;
                result.Weight = value.Weight;
                result.Starts = value.Starts;
                result.Version++;

            }
        }
    }
}
