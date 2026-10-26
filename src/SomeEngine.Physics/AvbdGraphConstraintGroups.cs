using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly struct AvbdGraphConstraints<TConstraint, TMath>(QueryHandle constraints) : IAvbdGraphConstraints
    where TConstraint : struct, IComponent where TMath : struct, IAvbdConstraint<TConstraint>
{
    private readonly AvbdBodyConstraints _byBody = new();
    public int QueryCount => 1;
    public QueryHandle GetQuery(int index) => constraints;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    {
        JobResourceToken token = WorldJobs.GetScheduler(world).GetResourceToken(_byBody);
        accesses.Add(phase == PhysicsGraphPhase.Initialize ? JobResourceAccess.Write(token) : JobResourceAccess.Read(token));
    }
    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int offset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        var read = new ReadParticipants(context, graph, offset);
        context.World.ExecuteReadQuery(queries[offset], ref read,
            static (scoped QueryCursor rows, ref ReadParticipants state) => state.Execute(rows));
    }
    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        AvbdConstraintRows<TConstraint, TMath>.Initialize(new(queries.Get(offset), queries.Get(2)), bodies, in context, retention, _byBody);
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdConstraintRows<TConstraint, TMath>.Accumulate(new(queries.Get(offset), queries.Get(2)), body, bodies,
            in context, initialErrorFraction, _byBody.Read(body), ref equation);
    public void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float initialErrorFraction) =>
        AvbdConstraintRows<TConstraint, TMath>.UpdateDuals(new(queries.Get(offset), queries.Get(2)), bodies, in context, initialErrorFraction);

    private ref struct ReadParticipants(PhysicsStepContext context, PhysicsConstraintGraphBuilder graph, int query)
    {
        private PhysicsConstraintGraphBuilder _graph = graph;
        public void Execute(scoped QueryCursor rows)
        { foreach (QueryRow row in rows.Rows) _graph.Add<TConstraint, TMath>(query, row, in context); }
    }
}

public readonly struct AvbdGraphConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IAvbdGraphConstraints
    where TFirst : struct, IAvbdGraphConstraints where TSecond : struct, IAvbdGraphConstraints
{
    public int QueryCount => first.QueryCount + second.QueryCount;
    public QueryHandle GetQuery(int index) => index < first.QueryCount ? first.GetQuery(index) : second.GetQuery(index - first.QueryCount);
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { first.AddResources(world, accesses, phase); second.AddResources(world, accesses, phase); }
    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int offset,
        ref PhysicsConstraintGraphBuilder graph)
    { first.ReadGraph(in context, queries, offset, ref graph); second.ReadGraph(in context, queries, offset + first.QueryCount, ref graph); }
    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float retention)
    { first.Initialize(in context, queries, offset, bodies, retention); second.Initialize(in context, queries, offset + first.QueryCount, bodies, retention); }
    public void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        Entity body, scoped PhysicsBodies bodies, float fraction, ref AvbdBodyEquation equation)
    {
        first.AccumulateBodyEquation(in context, queries, offset, body, bodies, fraction, ref equation);
        second.AccumulateBodyEquation(in context, queries, offset + first.QueryCount, body, bodies, fraction, ref equation);
    }
    public void AdjustBodyChange(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        Entity body, scoped PhysicsBodies bodies, in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    {
        first.AdjustBodyChange(in context, queries, offset, body, bodies, in equation, ref translation, ref rotation);
        second.AdjustBodyChange(in context, queries, offset + first.QueryCount, body, bodies, in equation, ref translation, ref rotation);
    }
    public void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float fraction)
    { first.UpdateDuals(in context, queries, offset, bodies, fraction); second.UpdateDuals(in context, queries, offset + first.QueryCount, bodies, fraction); }
}

public static class AvbdGraphConstraints
{
    public static AvbdGraphConstraintGroups<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IAvbdGraphConstraints where TSecond : struct, IAvbdGraphConstraints => new(first, second);
}
