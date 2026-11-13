using SomeEngine.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>XPBD projection of the CPU contact columns with one multiplier value per point.</summary>
public readonly partial struct XpbdContactConstraints(ContactManifolds<XpbdContactState> contacts) : IXpbdConstraints
{
    public void AddAccesses(World world, List<JobResourceAccess> accesses) => accesses.Add(contacts.WriteAccess);
    public void Initialize(in PhysicsStepContext context)
    {
        for (int index = 0; index < contacts.Columns.PointCount; index++) contacts.States[index] = default;
    }
    public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    {
        for (int index = 0; index < contacts.Columns.PointCount; index++) ProjectPoint(index, bodies, in context);
    }
    private void ProjectPoint(int index, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ContactColumns columns = contacts.Columns;
        if ((columns.Flags[index] & ContactPointFlags.Trigger) != 0
            || !AvbdContactConstraints<AvbdContactState, AvbdContactMath>.HasAwakeBody(columns.BodyLinksOf(index), bodies)) return;
        ContactPoint point = columns.GetPoint(index);
        XpbdContactProjection.Project(in point, columns.BodyLinksOf(index), in columns.Materials[index],
            ref contacts.States[index], bodies, in context, ref columns.Responses[index]);
    }
}
