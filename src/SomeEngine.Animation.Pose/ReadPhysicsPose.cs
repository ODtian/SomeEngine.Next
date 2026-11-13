using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

/// <summary>Maps completed body components into the existing pose before ordinary composition.</summary>
public readonly struct ReadPhysicsPose : IPhysicsStage
{
    private readonly PhysicsPoseSamples _samples;
    private readonly Query _bodies;
    internal ReadPhysicsPose(PhysicsPoseSamples samples, Query bodies) => (_samples, _bodies) = (samples, bodies);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs jobs = (WorldJobs)_samples.World.Execution!;
        JobHandle read = jobs.Scheduler.Schedule(new CollectBodies(_samples, _bodies), jobs.StructureRead, dependency);
        _samples.Step = new(context.Number, context.StartTime, context.EndTime, read);
        return read;
    }
    private readonly struct BodyReads : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<BodyPose>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows) { }
    }
    private readonly struct CollectBodies(PhysicsPoseSamples samples, Query bodies) : IJob
    {
        public void Execute()
        {
            WorldJobs jobs = (WorldJobs)samples.World.Execution!;
            JobResourceAccess[] accesses = [.. jobs.CollectAccess<BodyReads>(bodies), .. samples.BodyAccesses];
            jobs.Scheduler.Schedule(new ReadBodyModels(samples), accesses);
        }
    }
    private readonly struct ReadBodyModels(PhysicsPoseSamples samples) : IJob
    {
        public void Execute() => samples.ReadBodies();
    }
}
