using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Applies normal restitution impulses after the common velocity reconstruction.</summary>
public readonly struct ApplyContactRestitution : IPhysicsStage
{
    private readonly QueryHandle _bodies, _contacts;
    private readonly int _iterations;
    private readonly JobResourceAccess[] _accesses;

    public ApplyContactRestitution(World world, QueryHandle bodies, QueryHandle contacts, int iterations)
    {
        _bodies = bodies;
        _contacts = contacts;
        _iterations = iterations;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, bodies, accesses);
        PhysicsQueryAccess.AddAccesses(world, contacts, accesses);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new RestitutionJob(context, _bodies, _contacts, _iterations), _accesses, dependency);

    private readonly struct RestitutionJob(PhysicsStepContext context, QueryHandle bodies, QueryHandle contacts,
        int iterations) : IJob, IQueryExecution
    {
        public void Execute()
        {
            var execution = this;
            context.World.ExecuteQuery(bodies, ref execution);
        }

        public void Execute(scoped QueryCursor query)
        {
            var execution = new ContactImpulses(new PhysicsBodies(query), context, iterations);
            context.World.ExecuteQuery(contacts, ref execution);
        }
    }

    private readonly ref struct ContactImpulses(PhysicsBodies bodies, PhysicsStepContext context, int iterations) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
        {
            for (int iteration = 0; iteration < iterations; iteration++)
                foreach (QueryRow row in query.Rows) Apply(row, _bodies, in _context);
        }
    }

    private static void Apply(scoped QueryRow row, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
        ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
        ref ContactResponse state = ref row.ReadWrite<ContactResponse>();
        if (Vector3.Dot(state.ForceOnSecond, point.Normal) <= 0
            || state.InitialNormalVelocity >= -material.RestitutionThreshold || material.Restitution == 0) return;
        ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
        float mobility = 0;
        for (int index = 0; index < links.Length; index++)
        {
            if (!ContactBodyMath.IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = ContactBodyMath.GetBody(in links[index], bodies, in context);
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, point.Normal, out Vector3 linear, out Vector3 angular);
            mobility += BodyConstraintMath.GetWeight(in bodies.ReadMass(body), in bodies.ReadPose(body), linear, angular);
        }
        if (mobility == 0) return;
        float speed = ContactBodyMath.GetSpeed(in point, links, bodies, in context, false);
        float next = MathF.Max(0, state.RestitutionImpulse + (-material.Restitution * state.InitialNormalVelocity - speed) / mobility);
        float change = next - state.RestitutionImpulse;
        state.RestitutionImpulse = next;
        for (int index = 0; index < links.Length; index++)
        {
            if (!ContactBodyMath.IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = ContactBodyMath.GetBody(in links[index], bodies, in context);
            ref readonly BodyMass mass = ref bodies.ReadMass(body);
            if (mass.Motion != BodyMotion.Dynamic) continue;
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, point.Normal, out Vector3 linear, out Vector3 angular);
            ref BodyVelocity velocity = ref bodies.GetRow(body).ReadWrite<BodyVelocity>();
            velocity.Linear += linear * (mass.InverseMass * change);
            velocity.Angular += mass.ApplyInverseInertia(bodies.ReadPose(body).Rotation, angular) * change;
        }
    }
}
