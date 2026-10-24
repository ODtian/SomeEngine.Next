using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Completes concrete per-model flow batches before dependent pose nodes consume their results.</summary>
public struct FlowMatchingSystem : ISystem<ImmediateSystemContext>, ISystem<PoseNodeContext>
{
    private QueryHandle _flows;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _jobs = [];
        _flows = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<FlowMatchingInstance>().Read<GraphParameters>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        var frame = new FlowFrame { World = context.World };
        do
        {
            Evaluate(ref frame);
            frame.Level = frame.NextLevel;
        } while (frame.Level != int.MaxValue);
    }

    public void OnCreate(ref PoseNodeContext context)
    {
        ImmediateSystemContext frame = context.Frame;
        OnCreate(ref frame);
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        var frame = new FlowFrame { World = context.Frame.World, Level = context.Level };
        Evaluate(ref frame);
    }

    private void Evaluate(ref FlowFrame frame)
    {
        FlowMatchingGroups groups = FlowMatchingGroups.For(frame.World);
        groups.Bind();
        foreach (FlowMatchingBatch batch in groups.Batches)
            foreach (FlowMatchingState state in batch.States) state.Active = false;
        frame.NextLevel = int.MaxValue;
        frame.World.ExecuteQuery(_flows, ref frame, static (scoped QueryCursor rows, ref FlowFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref FlowMatchingInstance instance in row.Buffer<FlowMatchingInstance>().AsSpan())
                {
                    if (instance.Node.Level > frame.Level) frame.NextLevel = Math.Min(frame.NextLevel, instance.Node.Level);
                    if (instance.Node.Level == frame.Level) Gather(frame.World, row.Entity, ref instance);
                }
        });
        _jobs.Clear();
        foreach (FlowMatchingBatch batch in groups.Batches)
            if (batch.HasSamples) _jobs.Add(JobSystem.Schedule(new GeneratePose(batch), batch.Accesses));
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_flows, ref frame, static (scoped QueryCursor rows, ref FlowFrame frame) =>
        {
            World world = frame.World;
            foreach (QueryRow row in rows.Rows)
                foreach (ref FlowMatchingInstance instance in row.Buffer<FlowMatchingInstance>().AsSpan())
                {
                    if (!instance.Active || instance.Node.Level != frame.Level) continue;
                    FlowMatchingParameters parameters = instance.Definition.Parameters[instance.Index];
                    if (instance.Sampled)
                    {
                        var copy = (instance.State, instance.Output.Range, parameters.RootMotion);
                        world.ExecuteBufferWrite<LocalPose, (FlowMatchingState State, PoseRange Range, RootMotionMode RootMotion)>(
                            instance.Output.Entity, ref copy, static (poses, ref copy) =>
                            {
                                Span<BoneTransform> output = LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count));
                                copy.State.Pose.CopyTo(output);
                                if (copy.RootMotion == RootMotionMode.Extract) output[0] = copy.State.Skeleton.ReferencePose[0];
                            });
                    }
                    var value = (instance.Output.Index, instance.Time, instance.Weight, instance.Starts, instance.Sampled,
                        Motion: parameters.RootMotion == RootMotionMode.Extract ? instance.State.Motion : RootMotionDelta.Identity);
                    world.ExecuteBufferWrite<PoseResult, (int Index, double Time, float Weight, uint Starts, bool Sampled, RootMotionDelta Motion)>(
                        instance.Output.Entity, ref value, static (results, ref value) =>
                        {
                            ref PoseResult result = ref results[value.Index];
                            result.Weight = value.Weight;
                            if (!value.Sampled) return;
                            result.Time = value.Time;
                            result.Starts = value.Starts;
                            result.RootMotion = new(value.Motion.Transform * result.RootMotion.Transform);
                            result.Version++;
                        });
                    AnimationGraph.Submit(world, row.Entity, in instance.Placement, running: true);
                }
        });
    }

    private static void Gather(World world, Entity animation, ref FlowMatchingInstance instance)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in instance.Placement);
        instance.Active = instance.State.Active = time.Active;
        instance.Sampled = false;
        if (!time.Active) return;
        FlowMatchingParameters parameters = instance.Definition.Parameters[instance.Index];
        if (instance.Starts != time.Starts)
        {
            instance.Starts = time.Starts;
            instance.State.Reset(parameters.InitialHistory.Span, parameters.Seed);
            instance.Sampled = true;
        }
        instance.Weight = time.Weight;
        if (world.Has<AnimationLayers>(instance.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
        }
        instance.Time = time.Time.Time;
        instance.State.Delta = (float)time.Time.Delta;
        instance.State.Sample = time.Time.Delta > 0;
        instance.State.Motion = RootMotionDelta.Identity;
        instance.Sampled |= instance.State.Sample;
        GraphParameters graph = world.Read<GraphParameters>(animation);
        for (int value = 0; value < instance.State.Control.Length; value++)
            instance.State.Control[value] = graph.Values[parameters.ControlParameter + value];
        instance.State.SuppliedNoise = parameters.NoiseParameter >= 0;
        if (instance.State.SuppliedNoise)
            for (int value = 0; value < instance.State.Noise.Length; value++)
                instance.State.Noise[value] = graph.Values[parameters.NoiseParameter + value];
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_flows);
    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_flows);

    private struct FlowFrame
    {
        internal World World;
        internal int Level, NextLevel;
    }

    private readonly record struct GeneratePose(FlowMatchingBatch Batch) : IJob
    {
        public void Execute() => Batch.Evaluate();
    }
}
