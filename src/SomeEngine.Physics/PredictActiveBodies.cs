using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Begins every selected substep and writes motion only for awake or kinematic body rows.</summary>
public readonly struct PredictActiveBodies : IPhysicsStage, IDisposable
{
    private readonly ActivePredictionData _data;
    public PredictActiveBodies(World world, Query bodies, Vector3 acceleration, int rowsPerPacket = 64) =>
        _data = new(world, bodies, acceleration, rowsPerPacket);
    public ResumeActiveBodyPrediction Resume() => new(this);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _data.Schedule(in context, false, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool resume, JobHandle dependency) => _data.Schedule(in context, resume, dependency);
    public void Dispose() { }
}
public readonly struct ResumeActiveBodyPrediction(PredictActiveBodies prediction) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => prediction.Schedule(in context, true, dependency);
}
internal sealed class ActivePredictionData(World world, Query bodies, Vector3 acceleration, int rowsPerPacket)
{
    private readonly World _world = world;
    private readonly Query _bodies = bodies;
    private readonly Vector3 _acceleration = acceleration;
    private readonly int _rowsPerRange = rowsPerPacket;
    private readonly PhysicsQueryWindow _window = new(bodies);
    internal JobHandle Schedule(in PhysicsStepContext context, bool resume, JobHandle dependency) =>
        WorldJobs.GetScheduler(world).Schedule(new Capture(this, context, resume), ((WorldJobs)world.Execution!).StructureRead, dependency);
    private readonly struct Capture(ActivePredictionData data, PhysicsStepContext context, bool resume) : IJob
    {
        public void Execute()
        {
            Query members = data._bodies.Since(data._window.Begin(context));
            new SystemRunner(data._world, data._rowsPerRange).Schedule(members, new Predict(data, context, resume), parallel: true);
        }
    }
    private readonly struct Predict(ActivePredictionData data, PhysicsStepContext context, bool resume) : IChunkAction
    {
        private static readonly ChunkAccess[] Accesses = [ChunkAccess.Write<BodyPose>(), ChunkAccess.Write<BodyVelocity>(),
            ChunkAccess.Read<BodyMass>(), ChunkAccess.Write<BodyStepState>(), ChunkAccess.WriteOptional<BodyActivity>()];
        public static ReadOnlySpan<ChunkAccess> Components => Accesses;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => data._window.Accept(data._world.Version);
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<BodyPose> poses = rows.Read<BodyPose>();
            ReadOnlySpan<BodyMass> masses = rows.Read<BodyMass>();
            bool hasActivity = rows.TryRead<BodyActivity>(out ReadOnlySpan<BodyActivity> activities);
            for (int row = 0; row < rows.Count; row++)
            {
                ref readonly BodyMass mass = ref masses[row];
                if (!resume)
                {
                    BodyPrediction.Begin(in poses[row], in mass, ref rows.Write<BodyStepState>(row), context.Number, context.Substep, context.EndTime);
                    if (hasActivity) rows.Write<BodyActivity>(row).MotionPredicted = false;
                }
                if (mass.Motion == BodyMotion.Static) continue;
                bool moving = resume ? hasActivity && !activities[row].Sleeping && !activities[row].MotionPredicted
                    : mass.Motion == BodyMotion.Kinematic || !hasActivity || !activities[row].Sleeping;
                if (!moving) continue;
                BodyPrediction.Move(ref rows.Write<BodyPose>(row), ref rows.Write<BodyVelocity>(row), in mass,
                    ref rows.Write<BodyStepState>(row), data._acceleration, (float)context.Duration);
                if (hasActivity) rows.Write<BodyActivity>(row).MotionPredicted = true;
            }
        }
    }
}
