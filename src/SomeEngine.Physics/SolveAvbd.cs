using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveAvbd
{
    public static SolveAvbd<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, int iterations, float multiplierRetention, float initialErrorFraction)
        where TConstraints : struct, IAvbdConstraints => new(world, bodies, constraints, iterations,
            multiplierRetention, initialErrorFraction);
}

/// <summary>Coupled six-dimensional body descent followed by all selected constraints' ALM dual updates.</summary>
public readonly struct SolveAvbd<TConstraints> : IPhysicsStage where TConstraints : struct, IAvbdConstraints
{
    private readonly QueryHandle _bodies;
    private readonly TConstraints _constraints;
    private readonly int _iterations;
    private readonly float _retention;
    private readonly float _initialErrorFraction;
    private readonly JobResourceAccess[] _accesses;

    public SolveAvbd(World world, QueryHandle bodies, TConstraints constraints, int iterations,
        float multiplierRetention, float initialErrorFraction)
    {
        _bodies = bodies;
        _constraints = constraints;
        _iterations = iterations;
        _retention = multiplierRetention;
        _initialErrorFraction = initialErrorFraction;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, bodies, accesses);
        constraints.AddAccesses(world, accesses);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new SolveConstraints(context, _bodies,
            _constraints, _iterations, _retention, _initialErrorFraction), _accesses, dependency);

    private readonly struct SolveConstraints(PhysicsStepContext context, QueryHandle bodyQuery, TConstraints constraints,
        int iterations, float retention, float initialErrorFraction) : IJob, IQueryExecution
    {
        public void Execute()
        {
            var execution = this;
            context.World.ExecuteQuery(bodyQuery, ref execution);
        }

        public void Execute(scoped QueryCursor query)
        {
            var bodies = new PhysicsBodies(query);
            foreach (QueryRow row in query.Rows)
            {
                ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                if (mass.Motion == BodyMotion.Dynamic)
                    row.ReadWrite<AvbdBodyEquation>().Initialize(in mass);
            }
            constraints.Initialize(in context, bodies, retention);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                foreach (QueryRow row in query.Rows)
                {
                    ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                    if (mass.Motion != BodyMotion.Dynamic) continue;
                    ref BodyPose pose = ref row.ReadWrite<BodyPose>();
                    ref readonly BodyStepState state = ref row.ReadRef<BodyStepState>();
                    ref AvbdBodyEquation equation = ref row.ReadWrite<AvbdBodyEquation>();
                    equation.Reset(in pose, in mass, in state, (float)context.Duration);
                    constraints.AccumulateBodyEquation(in context, row.Entity, bodies, initialErrorFraction, ref equation);
                    equation.Factorize();
                    equation.SolveResponse(-equation.LinearGradient, -equation.AngularGradient,
                        out var translation, out var rotation);
                    var center = pose.GetCenterOfMass(in mass) + translation;
                    pose.Rotation = AvbdRotation.Advance(pose.Rotation, rotation);
                    pose.SetCenterOfMass(center, in mass);
                }
                constraints.UpdateDuals(in context, bodies, initialErrorFraction);
            }
        }
    }
}
