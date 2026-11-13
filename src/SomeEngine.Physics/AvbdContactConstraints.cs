using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>AVBD numerical work over the CPU manifold's original geometry and state columns.</summary>
public readonly partial struct AvbdContactConstraints<TState, TMath>(ContactManifolds<TState> contacts) : IAvbdConstraints
    where TState : unmanaged where TMath : struct, IAvbdContactMath<TState>
{
    public void AddAccesses(World world, List<JobResourceAccess> accesses) => accesses.Add(contacts.WriteAccess);
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        ContactColumns columns = contacts.Columns;
        for (int index = 0; index < columns.PointCount; index++)
        {
            if ((columns.Flags[index] & ContactPointFlags.Trigger) != 0 || !HasAwakeBody(columns.BodyLinksOf(index), bodies)) continue;
            ContactPoint point = columns.GetPoint(index);
            TMath.Initialize(in point, columns.BodyLinksOf(index), in columns.Materials[index], ref contacts.States[index],
                bodies, in context, retention);
        }
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ContactColumns columns = contacts.Columns;
        foreach (int pairIndex in columns.Contacts.OpenView().PairsOf(body))
        {
            ContactPair pair = columns.Pairs[pairIndex];
            for (int index = pair.FirstPoint; index < pair.FirstPoint + pair.PointCount; index++)
            {
                if ((columns.Flags[index] & ContactPointFlags.Trigger) != 0) continue;
                ReadOnlySpan<ContactBodyLink> links = columns.BodyLinksOf(index);
                bool participates = false;
                foreach (ContactBodyLink link in links) participates |= link.Body == body;
                if (!participates) continue;
                ContactPoint point = columns.GetPoint(index);
                TMath.AccumulateBodyEquation(in point, links, in columns.Materials[index], in contacts.ReadOnlyStates[index],
                    body, bodies, in context, initialErrorFraction, ref equation);
            }
        }
    }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        ContactColumns columns = contacts.Columns;
        for (int index = 0; index < columns.PointCount; index++)
        {
            if ((columns.Flags[index] & ContactPointFlags.Trigger) != 0 || !HasAwakeBody(columns.BodyLinksOf(index), bodies)) continue;
            ContactPoint point = columns.GetPoint(index);
            TMath.UpdateDuals(in point, columns.BodyLinksOf(index), in columns.Materials[index], ref contacts.States[index],
                bodies, in context, initialErrorFraction, ref columns.Responses[index]);
        }
    }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    { }
    internal static bool HasAwakeBody(ReadOnlySpan<ContactBodyLink> links, scoped PhysicsBodies bodies)
    {
        foreach (ContactBodyLink link in links)
            if (bodies.ReadMass(link.Body).Motion == BodyMotion.Dynamic && bodies.IsAwake(link.Body)) return true;
        return false;
    }
}
