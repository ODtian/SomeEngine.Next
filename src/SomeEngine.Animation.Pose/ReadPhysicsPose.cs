using System.Numerics;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

/// <summary>Maps actual body components to the existing bone models within the physics frame's consumption window.</summary>
public readonly struct ReadPhysicsPose : IPhysicsStage
{
    private readonly PhysicsPoseSamples _samples;
    private readonly QueryHandle _bodies;
    private readonly JobResourceAccess[] _accesses;

    internal ReadPhysicsPose(PhysicsPoseSamples samples, QueryHandle bodies)
    {
        _samples = samples;
        _bodies = bodies;
        QueryDefinition definition = samples.World.GetQueryDefinition(bodies);
        _accesses = new JobResourceAccess[QueryJobAccess.Count(definition) + samples.BodyAccesses.Length];
        int count = QueryJobAccess.CopyAccesses(samples.World, definition, _accesses);
        samples.BodyAccesses.CopyTo(_accesses, count);
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle read = PoseAnimation.GetScheduler(_samples.World).Schedule(
            new ReadBodyModels(_samples, _bodies, _samples.ActorTransform), _accesses, dependency);
        _samples.Step = new(context.Number, context.StartTime, context.EndTime, read);
        return read;
    }

    private readonly record struct ReadBodyModels(PhysicsPoseSamples Samples, QueryHandle Bodies, Matrix4x4 Actor) : IJob
    {
        public void Execute()
        {
            var read = this;
            Samples.World.ExecuteReadQuery(Bodies, ref read,
                static (scoped QueryCursor bodies, ref ReadBodyModels read) => read.Samples.ReadBodies(bodies, read.Actor));
        }
    }
}
