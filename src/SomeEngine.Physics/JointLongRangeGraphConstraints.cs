using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The original joint-bound components participating in graph-based body updates.</summary>
public readonly struct JointLongRangeGraphConstraints(QueryHandle joints, JointLongRangeSettings settings) : IAvbdGraphConstraints
{
    private readonly JointLongRangeData _data = new(settings);
    public int QueryCount => 1;
    public QueryHandle GetQuery(int index) => joints;
    public void ReadActivityErrors(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        _data.ReadActivityErrors(new(queries.Get(offset), queries.Get(2)), bodies, ref errors);

    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    {
        JobResourceToken resource = WorldJobs.GetScheduler(world).GetResourceToken(_data);
        accesses.Add(phase is PhysicsGraphPhase.Metadata or PhysicsGraphPhase.Initialize or PhysicsGraphPhase.Duals
            ? JobResourceAccess.Write(resource) : JobResourceAccess.Read(resource));
        if (phase == PhysicsGraphPhase.Metadata) PhysicsQueryAccess.AddAccesses(world, joints, accesses);
    }

    public void RefreshGraph(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies) =>
        _data.RefreshGraph(new(queries.Get(queryOffset), queries.Get(2)), bodies, in context);

    public void ReadGraph(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        Span<PhysicsConstraintBody> bodies = stackalloc PhysicsConstraintBody[2];
        Span<Entity> references = stackalloc Entity[1];
        foreach (QueryRow row in queries.Get(queryOffset).Rows)
        {
            ReadOnlySpan<LongRangeJointBound> bounds = row.ReadBuffer<LongRangeJointBound>().AsSpan();
            for (int index = 0; index < bounds.Length; index++)
            {
                ref readonly LongRangeJointBound bound = ref bounds[index];
                queries.Get(queryOffset).TryGetRow(bound.OtherJoint, out QueryRow other);
                var first = row.Read<DirectedRelationEndpoints<JointConstraint>>();
                var last = other.Read<DirectedRelationEndpoints<JointConstraint>>();
                bodies[0] = new(bound.FirstEnd == JointEnd.Source ? first.Source : first.Target, true);
                bodies[1] = new(bound.OtherEnd == JointEnd.Source ? last.Source : last.Target, true);
                references[0] = bound.OtherJoint;
                graph.Add(queryOffset - 3, row.Entity, index, bodies, references);
            }
        }
    }

    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float retention) =>
        _data.Initialize(new(queries.Get(queryOffset), queries.Get(2)), bodies, in context, refreshGraph: false);

    public void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        _data.Accumulate(new(queries.Get(queryOffset), queries.Get(2)), body, bodies, ref equation);

    public void AdjustBodyChange(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) { }

    public void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float initialErrorFraction) =>
        _data.Update(new(queries.Get(queryOffset), queries.Get(2)), bodies);


}

/// <summary>Reads each generated distance bound's bodies from its two original joint roles.</summary>
public readonly struct JointLongRangeBodies : IPhysicsConstraintBodies<JointConstraint>
{
    public static int GetBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<LongRangeJointBound>().Count * 2;

    public static Entity GetBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ref readonly LongRangeJointBound bound = ref row.ReadBuffer<LongRangeJointBound>().AsSpan()[index / 2];
        bool first = (index & 1) == 0;
        DirectedRelationEndpoints<JointConstraint> endpoints = first
            ? row.Read<DirectedRelationEndpoints<JointConstraint>>()
            : context.World.Read<DirectedRelationEndpoints<JointConstraint>>(bound.OtherJoint);
        JointEnd end = first ? bound.FirstEnd : bound.OtherEnd;
        return end == JointEnd.Source ? endpoints.Source : endpoints.Target;
    }

    public static int GetReadEntityCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<LongRangeJointBound>().Count;

    public static Entity GetReadEntity(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        row.ReadBuffer<LongRangeJointBound>().AsSpan()[index].OtherJoint;
}
