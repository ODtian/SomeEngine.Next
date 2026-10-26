using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Query references and reusable entity membership used by one graph solver.</summary>
internal sealed class PhysicsGraphQueries : IDisposable
{
    private readonly World _world;
    private List<Entity>[] _members, _next;
    private List<Entity> _dynamic = [], _nextDynamic = [];
    private readonly bool _bodyUpdates;
    internal readonly QueryHandle[] Queries, Reads;
    internal readonly JobResourceAccess[] MetadataAccesses;

    internal PhysicsGraphQueries(World world, QueryHandle bodies, ReadOnlySpan<QueryHandle> constraints, QueryReadResources? resources,
        bool bodyUpdates = false)
    {
        _world = world;
        _bodyUpdates = bodyUpdates;
        Queries = new QueryHandle[constraints.Length + 3];
        Reads = new QueryHandle[constraints.Length];
        _members = CreateMembers(constraints.Length + 1);
        _next = CreateMembers(constraints.Length + 1);
        QueryDefinition bodyDefinition = world.GetQueryDefinition(bodies);
        Queries[0] = world.Query(bodyDefinition.AsReadOnly());
        Queries[1] = world.Query(bodyDefinition);
        QueryDefinition references = QueryDefinition.Empty.WithAccessFrom(bodyDefinition.AsReadOnly());
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, Queries[0], accesses, resources);
        for (int index = 0; index < constraints.Length; index++)
        {
            QueryDefinition definition = world.GetQueryDefinition(constraints[index]);
            Queries[index + 3] = world.Query(definition);
            Reads[index] = world.Query(definition.AsReadOnly());
            references = references.WithAccessFrom(definition.AsReadOnly());
            PhysicsQueryAccess.AddAccesses(world, Reads[index], accesses, resources);
        }
        Queries[2] = world.Query(references);
        PhysicsQueryAccess.AddAccesses(world, Queries[2], accesses, resources);
        MetadataAccesses = accesses.ToArray();
    }

    private static List<Entity>[] CreateMembers(int count) => Enumerable.Range(0, count).Select(static _ => new List<Entity>()).ToArray();

    internal bool ReadMembers()
    {
        bool changed = false;
        _nextDynamic.Clear();
        for (int index = 0; index < _members.Length; index++)
        {
            List<Entity> next = _next[index];
            next.Clear();
            if (index == 0 && _bodyUpdates)
            {
                var state = (Members: next, Dynamic: _nextDynamic);
                _world.ExecuteReadQuery(Queries[0], ref state,
                    static (scoped QueryCursor rows, ref (List<Entity> Members, List<Entity> Dynamic) members) =>
                    {
                        foreach (QueryRow row in rows.Rows)
                        {
                            members.Members.Add(row.Entity);
                            if (row.ReadRef<BodyMass>().Motion == BodyMotion.Dynamic) members.Dynamic.Add(row.Entity);
                        }
                    });
            }
            else
                _world.ExecuteReadQuery(index == 0 ? Queries[0] : Reads[index - 1], ref next,
                    static (scoped QueryCursor rows, ref List<Entity> members) =>
                    { foreach (QueryRow row in rows.Rows) members.Add(row.Entity); });
            changed |= !CollectionsMarshal.AsSpan(next).SequenceEqual(CollectionsMarshal.AsSpan(_members[index]));
        }
        (_members, _next) = (_next, _members);
        changed |= !CollectionsMarshal.AsSpan(_dynamic).SequenceEqual(CollectionsMarshal.AsSpan(_nextDynamic));
        (_dynamic, _nextDynamic) = (_nextDynamic, _dynamic);
        return changed;
    }

    internal void ReadBodies(PhysicsGraphData graph) => _world.ExecuteReadQuery(Queries[0], ref graph,
        static (scoped QueryCursor rows, ref PhysicsGraphData data) => data.ReadBodies(rows));

    internal ReadOnlySpan<Entity> GlobalEntities(int query, PhysicsGraphData graph) => query switch
    {
        0 => CollectionsMarshal.AsSpan(_members[0]),
        1 => CollectionsMarshal.AsSpan(_bodyUpdates ? _dynamic : graph.Writes),
        2 => CollectionsMarshal.AsSpan(graph.References),
        _ => CollectionsMarshal.AsSpan(_members[query - 2]),
    };

    public void Dispose()
    {
        foreach (QueryHandle query in Reads) _world.ReleaseQuery(query);
        foreach (QueryHandle query in Queries) _world.ReleaseQuery(query);
    }
}
