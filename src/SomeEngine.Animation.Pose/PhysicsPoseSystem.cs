using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Blends mapped physics bone models and publishes masked local results before ordinary composition.</summary>
public struct PhysicsPoseSystem : ISystem<PoseNodeContext>
{
    private QueryHandle _instances;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _instances = context.Frame.World.Query(AnimationGraph.WithTimeAccess(context.Frame.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<PhysicsPoseInstance>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new PhysicsPoseFrame(context.Frame.World, context.Level, _jobs);
        frame.World.ExecuteQuery(_instances, ref frame, static (scoped QueryCursor rows, ref PhysicsPoseFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref PhysicsPoseInstance instance in row.Buffer<PhysicsPoseInstance>().AsSpan())
                    if (instance.Node.Level == frame.Level) Gather(frame.World, row.Entity, ref instance, frame.Jobs);
        });
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_instances, ref frame, static (scoped QueryCursor rows, ref PhysicsPoseFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref PhysicsPoseInstance instance in row.Buffer<PhysicsPoseInstance>().AsSpan())
                    if (instance.Node.Level == frame.Level && instance.Active) Publish(frame.World, row.Entity, ref instance);
        });
    }

    private static void Gather(World world, Entity animation, ref PhysicsPoseInstance instance, List<JobHandle> jobs)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in instance.Placement);
        instance.Active = time.Active;
        instance.Sampled = false;
        if (!time.Active) return;
        instance.InputResult = PoseAnimation.ReadResult(world, in instance.Source);
        instance.Weight = instance.InputResult.Weight > 0 ? time.Weight : 0;
        if (instance.Weight == 0) return;
        float physicsWeight = 1;
        if (world.Has<AnimationLayers>(instance.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) physicsWeight = layers.Items[instance.Placement.Layer].Weight;
        }
        var copy = (Range: instance.Source.Range, Values: instance.Samples.Source);
        world.ExecuteBufferRead<LocalPose, (PoseRange Range, BoneTransform[] Values)>(instance.Source.Entity, ref copy,
            static (poses, ref copy) => LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                .CopyTo(copy.Values));
        instance.Samples.ReadParents();
        instance.Sampled = true;
        jobs.Add(PoseAnimation.GetScheduler(world).Schedule(new BlendBodies(instance.Samples, physicsWeight),
            instance.Samples.Accesses, instance.Step?.Completion ?? default));
    }

    private static void Publish(World world, Entity animation, ref PhysicsPoseInstance instance)
    {
        if (instance.Sampled)
        {
            var copy = (Range: instance.Output.Range, Values: instance.Samples.Output);
            world.ExecuteBufferWrite<LocalPose, (PoseRange Range, BoneTransform[] Values)>(instance.Output.Entity, ref copy,
                static (poses, ref copy) => copy.Values.AsSpan()
                    .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
        }
        var output = (Index: instance.Output.Index, Source: instance.InputResult, Weight: instance.Weight, Sampled: instance.Sampled);
        world.ExecuteBufferWrite<PoseResult, (int Index, PoseResult Source, float Weight, bool Sampled)>(instance.Output.Entity,
            ref output, static (results, ref output) =>
            {
                ref PoseResult result = ref results[output.Index];
                result.Weight = output.Weight;
                if (!output.Sampled) return;
                result.Time = output.Source.Time;
                result.Phase = output.Source.Phase;
                result.Starts = output.Source.Starts;
                result.RootMotion = new(output.Source.RootMotion.Transform * result.RootMotion.Transform);
                result.Version++;
            });
        AnimationGraph.Submit(world, animation, in instance.Placement, instance.Sampled);
    }

    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_instances);

    private readonly record struct PhysicsPoseFrame(World World, int Level, List<JobHandle> Jobs);

    private readonly record struct BlendBodies(PhysicsPoseSamples Samples, float Weight) : IJob
    {
        public void Execute() => Samples.Blend(Weight);
    }
}
