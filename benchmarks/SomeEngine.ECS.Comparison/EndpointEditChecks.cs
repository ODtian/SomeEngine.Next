using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal static class EndpointEditChecks
{
    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private readonly record struct Link(int Value) : IComponent;

    internal static void Run()
    {
        foreach (bool deferred in new[] { false, true })
        using (var world = new World(chunkCapacity: 1))
        {
            Entity a = world.Create(), b = world.Create(), c = world.Create(), d = world.Create();
            var old = world.CreateRelation(a, b, new Link(1));
            var moved = world.CreateRelation(c, d, new Link(2));
            Relations<Link>.SetOrdered(world, a, RelationAdjacencyRole.Outgoing, true);
            ulong version = world.Version;
            var created = new RelationEdge<Link>[2];
            Relations<Link>.Edit(world,
                [EndpointEdit.Destroy(old.Entity), new(moved.Entity, c, b),
                    EndpointEdit.Create(a, EndpointEdit.CreatedInBatch(1)), EndpointEdit.Create(EndpointEdit.CreatedInBatch(0), d)],
                created, deferred);
            Require(world.Version == version + 1 && world.IsPendingCleanup(old.Entity)
                && world.Read<RelationEndpoints<Link>>(created[1].Entity).Source == created[0].Entity
                && world.Read<RelationEndpoints<Link>>(created[0].Entity).Target == created[1].Entity
                && world.Read<RelationEndpoints<Link>>(moved.Entity).Target == b && world.Read<Link>(moved.Entity).Value == 2,
                "the final batch replaces a claim and resolves a new edge used as an endpoint at one version");
            if (deferred) world.MaintainRelations<Link>();
            using (var outgoing = world.GetOutgoingRelations<Link>(a))
                Require(outgoing.Count == 1 && outgoing.Entries[0].Edge == created[0].Entity,
                    "ordered adjacency contains the new final claim");
            using (var outgoing = world.GetOutgoingRelations<Link>(created[0].Entity))
                Require(outgoing.Count == 1 && outgoing.Entries[0].Edge == created[1].Entity,
                    "new edge identities participate in the same batch's adjacency");

            int before = world.EntityCount;
            version = world.Version;
            bool rejected = false;
            try { Relations<Link>.Edit(world, [EndpointEdit.Create(a, b)], new RelationEdge<Link>[1]); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && world.EntityCount == before && world.Version == version,
                "final claim validation precedes identity and value publication");
        }
        CheckCommands();
        Console.WriteLine("Endpoint edits: new batch identities, create/retarget/destroy final claims, ordered and deferred adjacency, one version, producer results and successful prefixes passed.");
    }

    private static void CheckCommands()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create(), c = world.Create();
        using var commands = new CommandBatch(world, 2);
        DeferredEntity prefix = commands[0].Create(new Position { X = 1 });
        var first = new DeferredEntity[2];
        commands[0].EditEndpoints<Link>(
            [EndpointCommandEdit.Create(a, b), EndpointCommandEdit.Create(CommandEntity.CreatedInBatch(0), c)], first);
        commands[0].Set(first[0], new Link(5));
        DeferredEntity after = commands[0].Create(new Position { X = 7 });
        var second = new DeferredEntity[1];
        commands[1].EditEndpoints<Link>([EndpointCommandEdit.Create(after, prefix)], second);
        commands.Playback();
        commands.Clear();
        Require(world.Read<Link>(first[0].Resolve()).Value == 5
            && world.Read<RelationEndpoints<Link>>(first[1].Resolve()).Source == first[0].Resolve()
            && world.Read<RelationEndpoints<Link>>(second[0].Resolve()).Source == after.Resolve(),
            "ordinary and composite creates share producer ranges and survive Clear");

        commands[0].EditEndpoints<Link>([EndpointCommandEdit.Destroy(second[0].Resolve())]);
        commands.Playback();
        Require(world.IsPendingCleanup(second[0].Resolve())
            && world.Read<Removed<RelationEndpoints<Link>>>(second[0].Resolve()).Value.Source == after.Resolve(),
            "a composite destruction preserves the original endpoints through native removed facts");
        commands.Clear();

        DeferredEntity successful = commands[0].Create(new Position { X = 11 });
        var failed = new DeferredEntity[1];
        commands[0].EditEndpoints<Link>([EndpointCommandEdit.Create(a, c)], failed);
        bool rejected = false;
        try { commands.Playback(); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && commands[0].CompletedCommands == 1 && successful.TryResolve(out _)
            && !failed[0].TryResolve(out _), "a failed composite publishes only its preceding successful create range");

        commands.Clear();
        Entity expired = world.Create(), target = world.Create();
        world.Destroy(expired);
        DeferredEntity start = commands[0].Create(new Position { X = 13 });
        var completed = new DeferredEntity[1];
        commands[0].EditEndpoints<Link>([EndpointCommandEdit.Create(start, target)], completed);
        commands[0].Set(expired, new Position());
        rejected = false;
        try { commands.Playback(); } catch (ArgumentException) { rejected = true; }
        Require(rejected && commands[0].CompletedCommands == 2 && start.TryResolve(out _)
            && completed[0].TryResolve(out Entity edge) && world.Read<RelationEndpoints<Link>>(edge).Source == start.Resolve(),
            "a later failure publishes the complete preceding ordinary and composite creation ranges");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
}
