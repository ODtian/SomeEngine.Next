using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Original query inputs and body participation shared by numerical work and activity.</summary>
public interface IPhysicsGraphInputs
{
    int QueryCount { get; }
    QueryHandle GetQuery(int index);
    void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase) { }
    void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int queryOffset,
        ref PhysicsConstraintGraphBuilder graph);
    void ReadActivityErrors(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) { }
}
