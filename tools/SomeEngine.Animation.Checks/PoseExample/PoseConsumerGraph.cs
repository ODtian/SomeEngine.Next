using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks.PoseExample;

public readonly record struct ConsumerPoseParameters(AnimationClip Clip, PoseResultId Output, int Bone, Vector3 Translation);

[BufferCapacity(1)]
public struct ConsumerPoseInstance : IBufferElement
{
    public AnimationLeafBatch<ConsumerPoseParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    public BoneTransform[] Samples;
}

/// <summary>Example leaf: samples a ready clip and adds a caller-selected local-bone translation.</summary>
public readonly struct ConsumerPoseLeaf : IAnimationLeaf<ConsumerPoseParameters>
{
    public static MotionBoundary Describe(in ConsumerPoseParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<ConsumerPoseParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<ConsumerPoseInstance>(animation)) world.AddBuffer<ConsumerPoseInstance>(animation);
        var instance = new ConsumerPoseInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
            Samples = new BoneTransform[definitions.Parameters[index].Clip.Skeleton.BoneCount],
        };
        world.ExecuteBufferWrite<ConsumerPoseInstance, ConsumerPoseInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<ConsumerPoseInstance>(animation, static instances => instances.Clear());
}

public struct ConsumerPoseSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _leaves;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _leaves = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().ReadBuffer<ConsumerPoseInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_leaves, ref world, static (scoped QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ConsumerPoseInstance instance in row.ReadBuffer<ConsumerPoseInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(in row, in instance.Placement);
                    if (!time.Active) continue;
                    ConsumerPoseParameters parameters = instance.Definition.Parameters[instance.Index];
                    float elapsed = time.Time.Time;
                    parameters.Clip.SampleLocal(elapsed % parameters.Clip.Duration, instance.Samples);
                    instance.Samples[parameters.Bone].Translation += parameters.Translation;
                    var values = (Values: instance.Samples, Range: instance.Output.Range);
                    world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(instance.Output.Entity,
                        ref values, static (output, ref values) => values.Values.AsSpan().CopyTo(
                            LocalPose.Transforms(output.AsSpan().Slice(values.Range.Start, values.Range.Count))));
                    var sampled = (Index: instance.Output.Index, Time: elapsed, Phase: (double)elapsed / parameters.Clip.Duration,
                        Weight: time.Weight, Starts: time.Starts);
                    world.ExecuteBufferWrite<PoseResult, (int Index, float Time, double Phase, float Weight, uint Starts)>(
                        instance.Output.Entity, ref sampled, static (results, ref sampled) =>
                        {
                            ref PoseResult result = ref results[sampled.Index];
                            result.Time = sampled.Time; result.Phase = sampled.Phase;
                            result.Weight = sampled.Weight; result.Starts = sampled.Starts;
                            result.RootMotion = RootMotionDelta.Identity;
                            result.Version++;
                        });
                    AnimationGraph.Submit(in row, in instance.Placement, true);
                }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_leaves);
}

public readonly record struct ConsumerSwitchParameters(byte Parameter, float Threshold, int Samples, string Name);
public struct ConsumerSwitchState { public int Samples; public string? Name; }

public readonly struct ConsumerSwitchCondition : IAnimationCondition<ConsumerSwitchParameters, ConsumerSwitchState>
{
    public static void Initialize(in ConsumerSwitchParameters parameters, ref ConsumerSwitchState state) =>
        state = new() { Name = parameters.Name };

    public static bool Matches(in ConsumerSwitchParameters parameters, in GraphParameters inputs, ref ConsumerSwitchState state)
    {
        state.Samples = inputs.Values[parameters.Parameter] >= parameters.Threshold ? state.Samples + 1 : 0;
        return state.Samples >= parameters.Samples;
    }
}

internal sealed class PoseConsumerFlow : IDisposable
{
    private readonly CompiledGraph _graph;
    private readonly CompiledGraph _parentGraph;
    private readonly AnimationClip _clip, _acl, _parentClip;
    internal SkeletonInstance Parent { get; }
    internal SkeletonInstance First { get; }
    internal SkeletonInstance Second { get; }
    internal Entity FirstAnimation { get; }
    internal Entity SecondAnimation { get; }

    internal PoseConsumerFlow(World world, AnimationClip clip)
    {
        _clip = clip;
        _parentClip = new(clip.Skeleton.Parent!, 4,
            ClipSampler.Create<ConsumerPoseData, ConsumerPoseSampler>(new([.2f, 0, 0], [0])));
        AclClipData compressed = AclClipCompressor.Compress(clip);
        _acl = new(clip.Skeleton, clip.Duration, ClipSampler.Create<AclClipData, AclClipSampler>(compressed));
        var storage = new PoseStorage();
        Parent = storage.CreateInstance(_parentClip.Skeleton);
        First = storage.CreateInstance(clip.Skeleton, Parent);
        Second = storage.CreateInstance(clip.Skeleton, Parent);
        Entity parentTarget = PoseAnimation.CreateTarget(world, Parent);
        using (var parentBuilder = new AnimationBuilder(world))
        {
            parentBuilder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(_parentClip, new(0), Loop: true));
            _parentGraph = parentBuilder.Compile();
        }
        _parentGraph.Play(world, parentTarget);
        var inputs = new GraphParameters();
        using (var builder = new AnimationBuilder(world))
        {
            using (builder.Parallel())
            {
                using (var machine = builder.StateMachine(ref inputs))
                {
                    using (machine.State(0, initial: true))
                        builder.Leaf<ConsumerPoseParameters, ConsumerPoseLeaf>(Entity.Null,
                            new(clip, new(0), 1, new(0, 0, .2f)));
                    using (machine.State(1))
                        builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(_acl, new(1), Loop: true));
                    machine.Transition(0, 1,
                        GraphCondition.Create<ConsumerSwitchParameters, ConsumerSwitchState, ConsumerSwitchCondition>(
                            new(0, 1, 2, "Example action input")));
                }
                builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(0)), new(new(1))]), new(2), new(StageEnd.Settled)));
            }
            _graph = builder.Compile();
        }
        FirstAnimation = _graph.Play(world, PoseAnimation.CreateTarget(world, First, 3, new(2)));
        SecondAnimation = _graph.Play(world, PoseAnimation.CreateTarget(world, Second, 3, new(2)));
        PropertyAnimation.SetSpeed(world, SecondAnimation, .5f);
    }

    internal static void AddSystems(SystemGroup<ImmediateSystemContext> systems) =>
        PoseAnimation.AddSystems(systems,
            static composition => AnimationSystems.Add(composition, new ConsumerPoseSystem(), AnimationPhase.Composition),
            static nodes => nodes.Add(new PoseBlendSystem()));

    internal void SwitchFirst(World world)
    {
        GraphParameters inputs = world.Read<GraphParameters>(FirstAnimation);
        inputs.Values[0] = 1;
        world.Replace(FirstAnimation, inputs);
    }

    internal static void CheckPlayback(AnimationClip clip)
    {
        using var world = new World();
        using var flow = new PoseConsumerFlow(world, clip);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AddSystems(systems);
        Tick();
        Near(new(.15f, 1, .2f), flow.First.ModelPose.GetBone(2).Transform.Translation);
        Near(new(.1f, 1, .2f), flow.Second.ModelPose.GetBone(2).Transform.Translation);
        flow.SwitchFirst(world);
        Tick(); Tick(); Tick();
        Near(new(.4f, 1, 0), flow.First.ModelPose.GetBone(2).Transform.Translation);
        Near(new(.4f, 1, .2f), flow.Second.ModelPose.GetBone(2).Transform.Translation);
        Near(new(.9921977f, .12467473f, 0), Vector3.TransformNormal(Vector3.UnitX, flow.First.ModelPose.GetBone(2).Transform));
        Check(ReferenceEquals(flow.Parent, flow.First.ModelPose.GetSection(0).Instance) &&
              ReferenceEquals(flow.Parent, flow.Second.ModelPose.GetSection(0).Instance), "Shared parent pose sections must stay live.");
        flow.CheckPrediction();
        Console.WriteLine("POSE_CONSUMER leaf=ok condition=ok shared-parent=ok independent-playback=ok prediction=ok");

        void Tick()
        {
            var context = new ImmediateSystemContext(world, 0, 0, .25);
            systems.Update(ref context);
        }
    }

    internal void CheckPrediction()
    {
        var storage = new PoseStorage();
        using SkeletonInstance firstParent = storage.CreateInstance(_parentClip.Skeleton);
        using SkeletonInstance secondParent = storage.CreateInstance(_parentClip.Skeleton);
        var local = new BoneTransform[1];
        _parentClip.SampleLocal(1.25f, local); firstParent.Update(local, 1.25);
        _parentClip.SampleLocal(1.5f, local); secondParent.Update(local, 1.5);
        PosePath path = PosePath.Blend([new(PosePath.FromClip(_clip), new(.5f, .5f, 1)),
            new(PosePath.FromClip(_acl), new(.5f, .5f, 1))]);
        var request = new PosePredictionRequest(path, new(1), 7);
        var batch = new PosePredictionBatch([new(request, [1], firstParent.ModelPose), new(request, [1], secondParent.ModelPose)]);
        var values = new Matrix4x4[2];
        var times = new PosePredictionSample[2];
        ulong version = First.ModelPose.Version;
        batch.SampleModel([1.25, 1.5], 1, values, times);
        Near(new(.35f, 1, 0), values[0].Translation);
        Near(new(.5f, 1, 0), values[1].Translation);
        Near(new(.9980475f, .06245932f, 0), Vector3.TransformNormal(Vector3.UnitX, values[0]));
        Check(times[0].PathTime == .25f && times[1].PathTime == .5f && times[0].CalculatedAt == 1 &&
              times[0].Request.Conditions == 7 && First.ModelPose.Version == version, "Prediction must report its source clocks and retain the live pose.");
    }

    public void Dispose()
    {
        _graph.Dispose(); _parentGraph.Dispose();
        First.Dispose(); Second.Dispose(); Parent.Dispose();
    }

    internal static void Near(Vector3 expected, Vector3 actual) =>
        Check(Vector3.Distance(expected, actual) < .001f, $"Expected pose {expected}; actual {actual}.");
    private static void Check(bool value, string message) => PoseConsumerChecks.Check(value, message);
}
