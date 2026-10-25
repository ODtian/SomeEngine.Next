using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Concrete AVBD sets borrowing current rows for global preparation/duals and partitioned body updates.</summary>
public interface IAvbdGraphConstraints
{
    int QueryCount { get; }
    QueryHandle GetQuery(int index);
    void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase) { }
    void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int queryOffset,
        ref PhysicsConstraintGraphBuilder graph);
    void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float retention);
    void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation);
    void AdjustBodyChange(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) { }
    void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float initialErrorFraction);
}
