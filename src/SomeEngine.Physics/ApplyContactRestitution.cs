using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Applies normal restitution impulses after the common velocity reconstruction.</summary>
public readonly struct ApplyContactRestitution : IPhysicsStage
{
    private readonly Query _bodies;
    private readonly ContactColumns _contacts;
    private readonly BodyColumns _columns = new();
    private readonly int _iterations;
    public ApplyContactRestitution(World world, Query bodies, ContactColumns contacts, int iterations)
    {
        _bodies = bodies; _contacts = contacts; _iterations = iterations;
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle completed = PhysicsBodyJobs.Schedule<VelocityBodyFields, Restitution>(_bodies, _columns, in context, new Restitution(this), dependency);
        var step = new PhysicsStepResult(context.Number, context.StartTime - context.Substep * context.Duration,
            context.EndTime, completed);
        _contacts.SetStep(in step);
        return completed;
    }
    private readonly struct Restitution(ApplyContactRestitution stage) : IPhysicsBodyWork
    {
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => accesses.Add(stage._contacts.WriteAccess);
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            for (int iteration = 0; iteration < stage._iterations; iteration++)
                for (int point = 0; point < stage._contacts.PointCount; point++) Apply(stage._contacts, point, bodies, in context);
        }
    }

    private static void Apply(ContactColumns contacts, int pointIndex, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ContactPoint point = contacts.GetPoint(pointIndex);
        ref readonly ContactMaterial material = ref contacts.Materials[pointIndex];
        ref ContactResponse state = ref contacts.Responses[pointIndex];
        if (Vector3.Dot(state.ForceOnSecond, point.Normal) <= 0
            || state.InitialNormalVelocity >= -material.RestitutionThreshold || material.Restitution == 0) return;
        ReadOnlySpan<ContactBodyLink> links = contacts.BodyLinksOf(pointIndex);
        float mobility = 0;
        for (int index = 0; index < links.Length; index++)
        {
            if (!ContactBodyMath.IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = ContactBodyMath.GetBody(in links[index], bodies, in context);
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, point.Normal, out Vector3 linear, out Vector3 angular, normal: true);
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
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, point.Normal, out Vector3 linear, out Vector3 angular, normal: true);
            ref BodyVelocity velocity = ref bodies.WriteVelocity(body);
            velocity.Linear += linear * (mass.InverseMass * change);
            velocity.Angular += mass.ApplyInverseInertia(bodies.ReadPose(body).Rotation, angular) * change;
        }
    }
}
