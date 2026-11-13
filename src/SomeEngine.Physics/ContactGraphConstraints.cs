using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct AvbdContactConstraints<TState, TMath> : IAvbdGraphConstraints
    where TState : unmanaged where TMath : struct, IAvbdContactMath<TState>
{
    public int QueryCount => 1;
    public Query? GetQuery(int index) => null;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph) =>
        ContactGraphInputs.Read(contacts.Columns, firstSet, ref graph);
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        ContactGraphInputs.ReadErrors(contacts.Columns, bodies, in context, ref errors);
}
public readonly partial struct XpbdContactConstraints : IXpbdGraphConstraints
{
    public int QueryCount => 1;
    public Query? GetQuery(int index) => null;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph) =>
        ContactGraphInputs.Read(contacts.Columns, firstSet, ref graph);
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        ContactGraphInputs.ReadErrors(contacts.Columns, bodies, in context, ref errors);
    public void Project(in PhysicsStepContext context, int set, Entity entity, int element, scoped PhysicsBodies bodies, bool useSimd) =>
        ProjectPoint(element, bodies, in context);
}
internal static class ContactGraphInputs
{
    internal static void Read(ContactColumns contacts, int set, ref PhysicsConstraintGraphBuilder graph)
    {
        graph.BeginSet(set);
        int largest = 0;
        foreach (int count in contacts.BodyCounts) largest = Math.Max(largest, count);
        PhysicsConstraintBody[] scratch = ArrayPool<PhysicsConstraintBody>.Shared.Rent(largest);
        try
        {
            for (int index = 0; index < contacts.PointCount; index++)
            {
                if ((contacts.Flags[index] & ContactPointFlags.Trigger) != 0) continue;
                ReadOnlySpan<ContactBodyLink> links = contacts.BodyLinksOf(index);
                Span<PhysicsConstraintBody> bodies = scratch.AsSpan(0, links.Length);
                for (int link = 0; link < links.Length; link++) bodies[link] = new(links[link].Body, true);
                graph.Add(set, default, index, bodies, []);
            }
        }
        finally { ArrayPool<PhysicsConstraintBody>.Shared.Return(scratch); }
        graph.EndSet(set);
    }
    internal static void ReadErrors(ContactColumns contacts, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, ref PhysicsActivityErrors errors)
    {
        for (int index = 0; index < contacts.PointCount; index++)
        {
            if ((contacts.Flags[index] & ContactPointFlags.Trigger) != 0) continue;
            ContactPoint point = contacts.GetPoint(index);
            ReadOnlySpan<ContactBodyLink> links = contacts.BodyLinksOf(index);
            PhysicsConstraintError error = ContactBodyMath.GetActivityError(in point, links, bodies, in context);
            foreach (ContactBodyLink link in links) errors.Add(link.Body, in error);
        }
    }
}
