using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct ContactLongRangeConstraints : IAvbdGraphConstraints
{
    public int QueryCount => 2;
    public bool HasGraphChanges => _data.HasGraphChanges;
    public Query? GetQuery(int index) => index == 0 ? supportBodies : null;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph)
    {
        graph.BeginSet(firstSet);
        using var scope = supportBodies.OpenRead();
        Span<PhysicsConstraintBody> body = stackalloc PhysicsConstraintBody[1];
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities)
            {
                body[0] = new(entity, true); graph.Add(firstSet, entity, body, []);
            }
        graph.EndSet(firstSet);
        ContactGraphInputs.Read(contacts.Columns, firstSet + 1, ref graph);
        _data.HasGraphChanges = false;
    }
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        _data.ReadActivityErrors(bodies, in context, ref errors);
}
