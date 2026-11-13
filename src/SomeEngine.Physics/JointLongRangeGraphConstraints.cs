using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct JointLongRangeConstraints : IAvbdGraphConstraints
{
    public int QueryCount => 1;
    public Query? GetQuery(int index) => joints;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void RefreshGraph(in PhysicsStepContext context, scoped PhysicsBodies bodies) =>
        _data.RefreshGraph(context.World, bodies, in context);
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph)
    {
        graph.BeginSet(firstSet);
        using var scope = joints.OpenRead();
        using var endpoints = context.World.OpenRead<RelationEndpoints<JointConstraint>>();
        Span<PhysicsConstraintBody> bodies = stackalloc PhysicsConstraintBody[2];
        Span<Entity> references = stackalloc Entity[1];
        foreach (QueryChunk chunk in scope)
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                ReadOnlySpan<LongRangeJointBound> bounds = chunk.ReadBuffer<LongRangeJointBound>(row).AsSpan();
                for (int index = 0; index < bounds.Length; index++)
                {
                    ref readonly LongRangeJointBound bound = ref bounds[index];
                    var first = endpoints.Read(entity); var last = endpoints.Read(bound.OtherJoint);
                    bodies[0] = new(bound.FirstEnd == JointEnd.Source ? first.Source : first.Target, true);
                    bodies[1] = new(bound.OtherEnd == JointEnd.Source ? last.Source : last.Target, true);
                    references[0] = bound.OtherJoint;
                    graph.Add(firstSet, entity, index, bodies, references);
                }
            }
        graph.EndSet(firstSet);
    }
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        _data.ReadActivityErrors(context.World, bodies, ref errors);
}
