using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal static class RelationChecks
{
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly record struct Link(long Value) : IComponent;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private readonly record struct Exclusive(long Value) : IComponent;
    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private readonly record struct Contact(long Value) : IComponent;

    internal static void Run()
    {
        CheckDirected();
        CheckClaims();
        CheckUndirected();
        CheckOrdered();
        Console.WriteLine("Relation main components, native facts, directed and undirected adjacency, final claims, local order, deferred maintenance and lifetime: passed.");
    }

    private static void CheckDirected()
    {
        using var world = new World(4, chunkCapacity: 4);
        Entity a = world.Create(new Position()), b = world.Create(new Position()), c = world.Create(new Position());
        ulong before = world.Version;
        var first = world.CreateRelation(a, b, new Link(11));
        var second = world.CreateRelation(a, b, new Link(22));
        Require(first.Entity != second.Entity && world.Read<Link>(first.Entity).Value == 11
            && world.Query(new QueryDescription().WithAdded<RelationEndpoints<Link>>(before)).Count == 2,
            "parallel edges have independent identity, payload and native Added facts");
        Entries<Link>(world, a, false, new(b, first.Entity), new(b, second.Entity));
        Entries<Link>(world, b, true, new(a, first.Entity), new(a, second.Entity));
        before = world.Version;
        world.RetargetRelationDeferred(first, a, c);
        Require(world.Read<RelationEndpoints<Link>>(first.Entity).Target == c
            && world.Query(new QueryDescription().WithChanged<RelationEndpoints<Link>>(before)).Count == 1,
            "deferred retarget publishes the native main value and version");
        Entries<Link>(world, b, true, new(a, first.Entity), new(a, second.Entity));
        Entries<Link>(world, c, true);
        Reject(() => world.Set(first.Entity, new RelationEndpoints<Link>(a, b)));
        world.MaintainRelations<Link>();
        Entries<Link>(world, b, true, new RelationEntry(a, second.Entity));
        Entries<Link>(world, c, true, new RelationEntry(a, first.Entity));
        world.DestroyRelation(second);
        Require(world.Read<Removed<RelationEndpoints<Link>>>(second.Entity).Value == new RelationEndpoints<Link>(a, b),
            "edge destruction retains its native old endpoints");
        world.Destroy(a);
        Entries<Link>(world, c, true);
        Require(world.IsPendingCleanup(first.Entity) && world.IsPendingCleanup(second.Entity),
            "endpoint destruction completes incident adjacency and retains old values");
        world.ClearRemoved<RelationEndpoints<Link>>();
        world.MaintainRelations<Link>();
        Require(!world.IsAlive(first.Entity) && !world.IsAlive(second.Entity), "fact cleanup completes edge identities");
    }

    private static void CheckClaims()
    {
        using var world = new World(4);
        Entity a = world.Create(), b = world.Create(), c = world.Create(), d = world.Create();
        var first = world.CreateRelation(a, b, new Exclusive(1));
        var second = world.CreateRelation(c, d, new Exclusive(2));
        Reject(() => world.RetargetRelationImmediate(first, a, d));
        Require(world.Read<RelationEndpoints<Exclusive>>(first.Entity).Target == b, "cardinality validation precedes main-value publication");
        ulong before = world.Version;
        Relations<Exclusive>.Retarget(world, new RelationRetarget<Exclusive>[] { new(first, a, d), new(second, c, b) });
        Require(world.Version == before + 1 && world.Read<RelationEndpoints<Exclusive>>(first.Entity).Target == d
            && world.Read<RelationEndpoints<Exclusive>>(second.Entity).Target == b, "one batch validates final claims and publishes one version");
        Reject(() => Relations<Exclusive>.Retarget(world, new RelationRetarget<Exclusive>[] { new(first, a, b), new(second, c, b) }));
        Require(world.Read<RelationEndpoints<Exclusive>>(first.Entity).Target == d, "a final-claim conflict preserves the complete preceding mapping");
        world.DestroyDeferred(second.Entity);
        world.ClearRemoved<RelationEndpoints<Exclusive>>();
        Entity reused = world.Create(new Position());
        var replacement = world.CreateRelation(c, b, new Exclusive(3));
        world.MaintainRelations<Exclusive>();
        Require(world.IsAlive(reused) && world.Read<Exclusive>(replacement.Entity).Value == 3,
            "retired identities and new claims complete independently");
        Entries<Exclusive>(world, c, false, new RelationEntry(b, replacement.Entity));
        Entries<Exclusive>(world, b, true, new RelationEntry(c, replacement.Entity));
    }

    private static void CheckUndirected()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create();
        var loop = world.CreateRelation(a, a, new Contact(1));
        var pair = world.CreateRelation(a, b, new Contact(2));
        using (var incident = world.GetIncidentRelations<Contact>(a))
        {
            var entries = new List<RelationEntry>();
            foreach (RelationEntry entry in incident) entries.Add(entry);
            Require(entries.SequenceEqual(new RelationEntry[] { new(a, loop.Entity), new(b, pair.Entity) }),
                "an undirected self loop occupies one incident member");
        }
        world.RetargetRelationImmediate(pair, b, a);
        Require(Relations<Contact>.FindPair(world, a, b) == pair, "undirected pair lookup follows either endpoint role");
        world.Destroy(a);
        using var remaining = world.GetIncidentRelations<Contact>(b);
        Require(remaining.Count == 0, "endpoint destruction clears undirected incident members");
    }

    private static void CheckOrdered()
    {
        using var world = new World(4, chunkCapacity: 4);
        Entity a = world.Create(), b = world.Create(), x = world.Create(), y = world.Create();
        world.SetRelationAdjacencyOrder<Link>(a, RelationAdjacencyRole.Outgoing, RelationAdjacencyOrderPolicy.Ordered);
        world.SetRelationAdjacencyOrder<Link>(b, RelationAdjacencyRole.Outgoing, RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(a, x, new Link(1));
        var second = world.CreateRelation(a, y, new Link(2));
        var third = world.CreateRelation(b, x, new Link(3));
        var fourth = world.CreateRelation(b, y, new Link(4));
        world.RetargetRelationDeferred(second, b, y);
        world.RetargetRelationDeferred(first, b, x);
        world.ReorderRelationAdjacency(b, RelationAdjacencyRole.Outgoing, fourth, 0);
        world.MaintainRelations<Link>();
        OrderedEntries<Link>(world, b, new(y, fourth.Entity), new(x, third.Entity), new(y, second.Entity), new(x, first.Entity));
        world.DestroyRelation(third);
        OrderedEntries<Link>(world, b, new(y, fourth.Entity), new(y, second.Entity), new(x, first.Entity));
        Relations<Link>.SetOrdered(world, b, RelationAdjacencyRole.Outgoing, true, new[] { first.Entity, fourth.Entity, second.Entity });
        OrderedEntries<Link>(world, b, new(x, first.Entity), new(y, fourth.Entity), new(y, second.Entity));
    }

    private static void OrderedEntries<T>(World world, Entity endpoint, params RelationEntry[] expected) where T : struct
    {
        using var read = world.GetOutgoingRelations<T>(endpoint);
        Require(read.Entries.SequenceEqual(expected), "local order, deferred FIFO and moved positions agree after completion");
    }

    private static void Entries<T>(World world, Entity endpoint, bool incoming, params RelationEntry[] expected) where T : struct
    {
        using var read = incoming ? world.GetIncomingRelations<T>(endpoint) : world.GetOutgoingRelations<T>(endpoint);
        Require(read.Entries.ToArray().OrderBy(entry => entry.Edge.Id).SequenceEqual(expected.OrderBy(entry => entry.Edge.Id)),
            "both adjacency directions retain their full edge and endpoint identities");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("The typed relation entry must enforce this declaration.");
    }
    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
}
