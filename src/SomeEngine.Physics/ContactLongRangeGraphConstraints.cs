using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Selected body supports and original contacts sharing the graph solver's current body decomposition.</summary>
public readonly struct ContactLongRangeGraphConstraints(QueryHandle supportBodies, QueryHandle contacts,
    ContactSupportSettings settings) : IAvbdGraphConstraints
{
    private readonly ContactSupportData _data = new(settings);
    public int QueryCount => 2;
    public QueryHandle GetQuery(int index) => index == 0 ? supportBodies : contacts;
    public void ReadActivityErrors(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        _data.ReadActivityErrors(new(queries.Get(offset)), new(queries.Get(offset + 1), queries.Get(2)), bodies, in context, ref errors);

    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    {
        JobResourceToken resource = WorldJobs.GetScheduler(world).GetResourceToken(_data);
        accesses.Add(phase is PhysicsGraphPhase.Initialize or PhysicsGraphPhase.Duals
            ? JobResourceAccess.Write(resource) : JobResourceAccess.Read(resource));
    }

    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int queryOffset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        var bodies = new ReadBodyParticipants(context, graph, queryOffset);
        context.World.ExecuteReadQuery(queries[queryOffset], ref bodies,
            static (scoped QueryCursor rows, ref ReadBodyParticipants state) => state.Execute(rows));
        var points = new ReadContactParticipants(context, graph, queryOffset + 1);
        context.World.ExecuteReadQuery(queries[queryOffset + 1], ref points,
            static (scoped QueryCursor rows, ref ReadContactParticipants state) => state.Execute(rows));
    }

    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float retention) =>
        _data.Initialize(new(queries.Get(queryOffset)), new(queries.Get(queryOffset + 1), queries.Get(2)), bodies, in context);

    public void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation) { }

    public void AdjustBodyChange(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        Entity body, scoped PhysicsBodies bodies, in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) =>
        _data.Correct(new(queries.Get(queryOffset)), new(queries.Get(queryOffset + 1), queries.Get(2)), body, bodies, in context,
            in equation, ref translation, ref rotation);

    public void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int queryOffset,
        scoped PhysicsBodies bodies, float initialErrorFraction) =>
        _data.Update(new(queries.Get(queryOffset)), new(queries.Get(queryOffset + 1), queries.Get(2)), bodies, in context);

    private ref struct ReadBodyParticipants(PhysicsStepContext context, PhysicsConstraintGraphBuilder graph, int query)
    {
        private PhysicsConstraintGraphBuilder _graph = graph;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows) _graph.Add<LongRangeBodySupport, BodySupportBodies>(query, row, in context);
        }
    }
    private ref struct ReadContactParticipants(PhysicsStepContext context, PhysicsConstraintGraphBuilder graph, int query)
    {
        private PhysicsConstraintGraphBuilder _graph = graph;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows) _graph.Add<ContactPoint, ContactLongRangeBodies>(query, row, in context);
        }
    }
}

/// <summary>A body support's own state and the original contacts carrying its load.</summary>
public readonly struct BodySupportBodies : IPhysicsConstraintBodies<LongRangeBodySupport>
{
    public static int GetBodyCount(in LongRangeBodySupport support, scoped QueryRow row, in PhysicsStepContext context) => 1;
    public static Entity GetBody(in LongRangeBodySupport support, scoped QueryRow row, int index, in PhysicsStepContext context) => row.Entity;
    public static int GetReadEntityCount(in LongRangeBodySupport support, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<LongRangeSupportLoad>().Count;
    public static Entity GetReadEntity(in LongRangeBodySupport support, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        row.ReadBuffer<LongRangeSupportLoad>().AsSpan()[index].Contact;
}

/// <summary>Original contact participants and lower contacts referenced by the generated load path.</summary>
public readonly struct ContactLongRangeBodies : IPhysicsConstraintBodies<ContactPoint>
{
    public static int GetBodyCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactMath.GetBodyCount(in point, row, in context);
    public static Entity GetBody(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactMath.GetBody(in point, row, index, in context);
    public static bool TryGetBody(in ContactPoint point, scoped QueryRow row, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Entity body) => AvbdContactMath.TryGetBody(in point, row, index, bodies, in context, out body);
    public static int GetReadEntityCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<ContactBodyLink>().Count + row.ReadBuffer<LongRangeSupportLoad>().Count;
    public static Entity GetReadEntity(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
        return index < links.Length ? links[index].Participant.Entity : row.ReadBuffer<LongRangeSupportLoad>().AsSpan()[index - links.Length].Contact;
    }
}
