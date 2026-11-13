using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Converts completed physics poses and publishes masked local results before ordinary composition.</summary>
public struct PhysicsPoseSystem : ISystem<PoseNodeContext>
{
    private Query _instances;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _instances = context.Frame.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<PhysicsPoseInstance>>()
            .Optional<AnimationLayers>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new PhysicsPoseFrame(context.Frame.World, context.Level, _jobs);
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref PhysicsPoseFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<PhysicsPoseInstance>>() ? chunk.GetBuffers<PhysicsPoseInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref PhysicsPoseInstance instance in buffers0[rowIndex].AsSpan())
                            if (instance.Node.Level == frame.Level) Gather(frame.World, chunk.Entities[rowIndex], ref instance, frame.Jobs);
                    }
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref PhysicsPoseFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<PhysicsPoseInstance>>() ? chunk.GetBuffers<PhysicsPoseInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref PhysicsPoseInstance instance in buffers0[rowIndex].AsSpan())
                            if (instance.Node.Level == frame.Level && instance.Active) Publish(frame.World, chunk.Entities[rowIndex], ref instance);
                    }
                }

            }
        }
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
            AnimationLayers layers = world.ReadValue<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) physicsWeight = layers.Items[instance.Placement.Layer].Weight;
        }
        var copy = (Range: instance.Source.Range, Values: instance.Samples.Source);
        {
            using var poses = world.ReadBuffer<LocalPose>(instance.Source.Entity);
            Visit(poses, ref copy);

            static void Visit(ReadOnlyBuffer<LocalPose> poses, ref (PoseRange Range, BoneTransform[] Values) copy)
            {
                LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                                .CopyTo(copy.Values);

            }
        }
        instance.Samples.ReadParents();
        instance.Sampled = true;
        jobs.Add(JobSystem.Schedule(new BlendBodies(instance.Samples, instance.ActorTransform, physicsWeight), instance.Samples.Accesses));
    }

    private static void Publish(World world, Entity animation, ref PhysicsPoseInstance instance)
    {
        if (instance.Sampled)
        {
            var copy = (Range: instance.Output.Range, Values: instance.Samples.Output);
            {
                using var poses = world.GetBuffer<LocalPose>(instance.Output.Entity);
                Visit(poses, ref copy);

                static void Visit(DynamicBuffer<LocalPose> poses, ref (PoseRange Range, BoneTransform[] Values) copy)
                {
                    copy.Values.AsSpan()
                                        .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count)));

                }
            }
        }
        var output = (Index: instance.Output.Index, Source: instance.InputResult, Weight: instance.Weight, Sampled: instance.Sampled);
        {
            using var results = world.GetBuffer<PoseResult>(instance.Output.Entity);
            Visit(results, ref output);

            static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, PoseResult Source, float Weight, bool Sampled) output)
            {
                ref PoseResult result = ref results[output.Index];
                result.Weight = output.Weight;
                if (!output.Sampled) return;
                result.Time = output.Source.Time;
                result.Phase = output.Source.Phase;
                result.Starts = output.Source.Starts;
                result.RootMotion = new(output.Source.RootMotion.Transform * result.RootMotion.Transform);
                result.Version++;

            }
        }
        AnimationGraph.Submit(world, animation, in instance.Placement, instance.Sampled);
    }

    public void OnDestroy(ref PoseNodeContext context) { }

    private readonly record struct PhysicsPoseFrame(World World, int Level, List<JobHandle> Jobs);

    private readonly record struct BlendBodies(PhysicsPoseSamples Samples, Matrix4x4 Actor, float Weight) : IJob
    {
        public void Execute() => Samples.Blend(Actor, Weight);
    }
}
