using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Original query inputs and body participation shared by numerical work and activity.</summary>
public interface IPhysicsGraphInputs : IPhysicsConstraintAccess
{
    int QueryCount { get; }
    Query? GetQuery(int index);
    bool HasGraphChanges => false;
    void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase) { }
    void RefreshGraph(in PhysicsStepContext context, scoped PhysicsBodies bodies) { }
    void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph);
    void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) { }
}
