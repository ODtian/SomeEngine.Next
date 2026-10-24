using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

public struct InertialPoseSystem : ISystem<PoseNodeContext>
{
    private QueryHandle _instances;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _instances = context.Frame.World.Query(AnimationGraph.WithTimeAccess(context.Frame.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<InertialPoseInstance>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new InertialFrame(context.Frame.World, context.Level, (float)context.Frame.DeltaTime, _jobs);
        frame.World.ExecuteQuery(_instances, ref frame, static (scoped QueryCursor rows, ref InertialFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref InertialPoseInstance instance in row.Buffer<InertialPoseInstance>().AsSpan())
                    if (instance.Node.Level == frame.Level) Gather(row.Entity, ref instance, in frame);
        });
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_instances, ref frame, static (scoped QueryCursor rows, ref InertialFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref InertialPoseInstance instance in row.Buffer<InertialPoseInstance>().AsSpan())
                    if (instance.Node.Level == frame.Level && instance.Active) Publish(frame.World, row.Entity, ref instance);
        });
    }

    private static void Gather(Entity animation, ref InertialPoseInstance instance, in InertialFrame frame)
    {
        World world = frame.World;
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in instance.Placement);
        instance.Active = time.Active;
        instance.Sampled = false;
        if (instance.Starts != time.Starts)
        {
            instance.Starts = time.Starts;
            instance.Initialized = false;
        }
        if (!time.Active) return;
        instance.InputResult = PoseAnimation.ReadResult(world, in instance.Source);
        instance.Weight = instance.InputResult.Weight > 0 ? time.Weight : 0;
        if (world.Has<AnimationLayers>(instance.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
        }
        if (instance.Weight == 0) return;
        var source = (Range: instance.Source.Range, Values: instance.Samples.Source);
        world.ExecuteBufferRead<LocalPose, (PoseRange Range, BoneTransform[] Values)>(instance.Source.Entity, ref source,
            static (poses, ref source) => LocalPose.Transforms(poses.AsSpan().Slice(source.Range.Start, source.Range.Count))
                .CopyTo(source.Values));
        InertialPoseParameters parameters = instance.Definition.Parameters[instance.Index];
        float delta = world.Read<Timeline>(animation).Paused ? 0 : frame.Delta;
        frame.Jobs.Add(JobSystem.Schedule(new InertializeBones(instance.Samples, parameters.DecayRate, parameters.Tolerance,
            delta, !instance.Initialized, instance.Action != instance.PreviousAction), instance.Samples.Accesses));
        instance.Sampled = true;
    }

    private static void Publish(World world, Entity animation, ref InertialPoseInstance instance)
    {
        if (instance.Sampled)
        {
            var copy = (Range: instance.Output.Range, Values: instance.Samples.Output);
            world.ExecuteBufferWrite<LocalPose, (PoseRange Range, BoneTransform[] Values)>(instance.Output.Entity, ref copy,
                static (poses, ref copy) => copy.Values.AsSpan()
                    .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
            instance.Initialized = true;
            instance.PreviousAction = instance.Action;
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

    private readonly record struct InertialFrame(World World, int Level, float Delta, List<JobHandle> Jobs);

    private readonly record struct InertializeBones(InertialPoseSamples Samples, float Rate, float Tolerance,
        float Delta, bool Initialize, bool Transition) : IJob
    {
        public void Execute()
        {
            if (Initialize)
            {
                Samples.Source.CopyTo(Samples.Output, 0);
                Samples.SourceVelocities.CopyTo(Samples.Velocities, 0);
                Array.Clear(Samples.Offsets);
                return;
            }
            float attenuation = MathF.Exp(-Rate * Delta);
            float squaredTolerance = Tolerance * Tolerance;
            for (int bone = 0; bone < Samples.Output.Length; bone++)
            {
                ref InertialBoneOffset offset = ref Samples.Offsets[bone];
                BoneTransform source = Samples.Source[bone];
                BoneVelocity velocity = Samples.SourceVelocities[bone];
                if (Transition)
                {
                    BoneTransform previous = Samples.Output[bone];
                    BoneVelocity previousVelocity = Samples.Velocities[bone];
                    previous.Translation += previousVelocity.Translation * Delta;
                    previous.Rotation = InertialPoseMath.ToQuaternion(previousVelocity.Angular * Delta) * previous.Rotation;
                    previous.Scale *= InertialPoseMath.Exp(previousVelocity.LogScale * Delta);
                    Quaternion rotation = Quaternion.Normalize(previous.Rotation * Quaternion.Conjugate(source.Rotation));
                    offset.Translation = previous.Translation - source.Translation;
                    offset.Rotation = InertialPoseMath.ToRotationVector(rotation);
                    offset.LogScale = InertialPoseMath.Log(previous.Scale / source.Scale);
                    offset.TranslationRate = previousVelocity.Translation - velocity.Translation;
                    offset.RotationRate = InertialPoseMath.ToRotationVectorRate(offset.Rotation,
                        previousVelocity.Angular - Vector3.Transform(velocity.Angular, rotation));
                    offset.LogScaleRate = previousVelocity.LogScale - velocity.LogScale;
                }
                else if (offset.Moving)
                {
                    Decay(ref offset.Translation, ref offset.TranslationRate, Rate, Delta, attenuation);
                    Decay(ref offset.Rotation, ref offset.RotationRate, Rate, Delta, attenuation);
                    Decay(ref offset.LogScale, ref offset.LogScaleRate, Rate, Delta, attenuation);
                }
                if (Transition || offset.Moving)
                    offset.Moving = offset.Translation.LengthSquared() > squaredTolerance || offset.TranslationRate.LengthSquared() > squaredTolerance
                        || offset.Rotation.LengthSquared() > squaredTolerance || offset.RotationRate.LengthSquared() > squaredTolerance
                        || offset.LogScale.LengthSquared() > squaredTolerance || offset.LogScaleRate.LengthSquared() > squaredTolerance;
                if (!offset.Moving)
                {
                    offset = default;
                    Samples.Output[bone] = source;
                    Samples.Velocities[bone] = velocity;
                    continue;
                }
                Quaternion correction = InertialPoseMath.ToQuaternion(offset.Rotation);
                Samples.Output[bone] = new()
                {
                    Translation = source.Translation + offset.Translation,
                    Rotation = Quaternion.Normalize(correction * source.Rotation),
                    Scale = source.Scale * InertialPoseMath.Exp(offset.LogScale),
                };
                Samples.Velocities[bone] = new(velocity.Translation + offset.TranslationRate,
                    Vector3.Transform(velocity.Angular, correction) + InertialPoseMath.ToAngularVelocity(offset.Rotation, offset.RotationRate),
                    velocity.LogScale + offset.LogScaleRate);
            }
        }

        private static void Decay(ref Vector3 displacement, ref Vector3 velocity, float rate, float delta, float attenuation)
        {
            Vector3 slope = velocity + rate * displacement;
            displacement = (displacement + slope * delta) * attenuation;
            velocity = (velocity - rate * slope * delta) * attenuation;
        }
    }
}
