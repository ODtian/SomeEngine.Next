using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>Concrete projection sets sharing their original row kernels with the entity graph.</summary>
public interface IXpbdGraphConstraints : IPhysicsGraphInputs
{
    void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset);
    void Project(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, bool useSimd);
}

public readonly struct XpbdGraphConstraints<TConstraint, TProjection>(QueryHandle constraints) : IXpbdGraphConstraints
    where TConstraint : struct, IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>, IPhysicsConstraintBodies<TConstraint>
{
    public int QueryCount => 1;
    public QueryHandle GetQuery(int index) => constraints;

    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int queryOffset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        var read = new ReadParticipants(context, graph, queryOffset);
        context.World.ExecuteReadQuery(queries[queryOffset], ref read,
            static (scoped QueryCursor rows, ref ReadParticipants state) => state.Execute(rows));
    }

    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset) =>
        XpbdConstraintRows<TConstraint, TProjection>.Initialize(new(queries.Get(queryOffset)), in context);

    public void Project(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, bool useSimd)
    {
        EntityQuery rows = queries.Get(queryOffset);
        int processed = useSimd ? TProjection.ProjectRange(rows, bodies, in context) : 0;
        foreach (var entity in rows.Entities[processed..])
        {
            if (rows.TryGetRow(entity, out QueryRow row))
                TProjection.Project(in row.ReadRef<TConstraint>(), row, bodies, in context);
        }
    }

    private ref struct ReadParticipants(PhysicsStepContext context, PhysicsConstraintGraphBuilder graph, int query)
    {
        private PhysicsConstraintGraphBuilder _graph = graph;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows) _graph.Add<TConstraint, TProjection>(query, row, in context);
        }
    }
}

public readonly struct XpbdGraphConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IXpbdGraphConstraints
    where TFirst : struct, IXpbdGraphConstraints where TSecond : struct, IXpbdGraphConstraints
{
    public int QueryCount => first.QueryCount + second.QueryCount;
    public QueryHandle GetQuery(int index) => index < first.QueryCount ? first.GetQuery(index) : second.GetQuery(index - first.QueryCount);
    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int offset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        first.ReadGraph(in context, queries, offset, ref graph);
        second.ReadGraph(in context, queries, offset + first.QueryCount, ref graph);
    }
    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset)
    {
        first.Initialize(in context, queries, offset);
        second.Initialize(in context, queries, offset + first.QueryCount);
    }
    public void Project(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, bool useSimd)
    {
        first.Project(in context, queries, offset, bodies, useSimd);
        second.Project(in context, queries, offset + first.QueryCount, bodies, useSimd);
    }
}

public static class XpbdGraphConstraints
{
    public static XpbdGraphConstraintGroups<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IXpbdGraphConstraints where TSecond : struct, IXpbdGraphConstraints => new(first, second);
}
