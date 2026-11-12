using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

public struct InertialPoseSystem : ISystem<PoseNodeContext>
{
    private Query _instances;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _instances = context.Frame.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<InertialPoseInstance>>()
            .Optional<AnimationLayers>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new InertialFrame(context.Frame.World, context.Level, (float)context.Frame.DeltaTime, _jobs);
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref InertialFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<InertialPoseInstance>>() ? chunk.GetBuffers<InertialPoseInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref InertialPoseInstance instance in buffers0[rowIndex].AsSpan())
                            if (instance.Node.Level == frame.Level) Gather(chunk.Entities[rowIndex], ref instance, in frame);
                    }
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref InertialFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<InertialPoseInstance>>() ? chunk.GetBuffers<InertialPoseInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref InertialPoseInstance instance in buffers0[rowIndex].AsSpan())
                            if (instance.Node.Level == frame.Level && instance.Active) Publish(frame.World, chunk.Entities[rowIndex], ref instance);
                    }
                }

            }
        }
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
            AnimationLayers layers = world.ReadComponentData<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
        }
        if (instance.Weight == 0) return;
        var source = (Range: instance.Source.Range, Values: instance.Samples.Source);
        {
            using var poses = world.ReadBuffer<LocalPose>(instance.Source.Entity);
            Visit(poses, ref source);

            static void Visit(ReadOnlyBuffer<LocalPose> poses, ref (PoseRange Range, BoneTransform[] Values) source)
            {
                LocalPose.Transforms(poses.AsSpan().Slice(source.Range.Start, source.Range.Count))
                                .CopyTo(source.Values);

            }
        }
        InertialPoseParameters parameters = instance.Definition.Parameters[instance.Index];
        float delta = world.ReadComponentData<Timeline>(animation).Paused ? 0 : frame.Delta;
        frame.Jobs.Add(PoseAnimation.GetScheduler(world).Schedule(new InertializeBones(instance.Samples, parameters.DecayRate, parameters.Tolerance,
            delta, !instance.Initialized, instance.Action != instance.PreviousAction), instance.Samples.Accesses));
        instance.Sampled = true;
    }

    private static void Publish(World world, Entity animation, ref InertialPoseInstance instance)
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
            instance.Initialized = true;
            instance.PreviousAction = instance.Action;
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
