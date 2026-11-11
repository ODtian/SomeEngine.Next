using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal static class CapturedTopologyChecks
{
    private struct Tree : IHierarchyDomain;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private struct Link : IComponent;

    internal static void Run()
    {
        ParentSegmentsKeepTheirConditions();
        ParentFailureKeepsTheCompletedPrefix();
        EndpointCreatesResolveWithinTheBatch();
        EndpointFailureKeepsTheCompletedPrefix();
        Console.WriteLine("Captured topology: independent conditions, FIFO prefixes and deferred create results passed.");
    }

    private static void ParentSegmentsKeepTheirConditions()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create(), c = world.Create(), first = world.Create(), second = world.Create();
        Hierarchy<Tree>.SetParent(world, first, a);
        Hierarchy<Tree>.SetParent(world, second, a);
        using var commands = new CommandBuffer(world);
        commands.EditParents<Tree>([new CapturedParentEdit(first, a, b)], world.DataGeneration);
        commands.EditParents<Tree>([new CapturedParentEdit(second, a, c)], world.DataGeneration);
        commands.Playback();
        Require(Hierarchy<Tree>.GetParent(world, first) == b && Hierarchy<Tree>.GetParent(world, second) == c,
            "each captured parent composite uses its own conditions");
    }

    private static void ParentFailureKeepsTheCompletedPrefix()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create(), c = world.Create(), child = world.Create();
        Hierarchy<Tree>.SetParent(world, child, a);
        using var commands = new CommandBuffer(world);
        commands.EditParents<Tree>([new ParentCommandEdit(child, b)]);
        commands.EditParents<Tree>([new CapturedParentEdit(child, a, c)], world.DataGeneration);
        bool failed = false;
        try { commands.Playback(); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && commands.CompletedCommands == 1 && Hierarchy<Tree>.GetParent(world, child) == b,
            "a changed parent rejects the conditional composite after its visible prefix");
    }

    private static void EndpointCreatesResolveWithinTheBatch()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create();
        using var commands = new CommandBatch(world, 1);
        var created = new DeferredEntity[2];
        commands[0].EditEndpoints<Link>(
            [new CapturedEndpointEdit(default, Entity.Null, Entity.Null, a, CommandEntity.CreatedInBatch(1), EndpointEditKind.Create),
             new CapturedEndpointEdit(default, Entity.Null, Entity.Null, CommandEntity.CreatedInBatch(0), b, EndpointEditKind.Create)],
            created, world.DataGeneration);
        commands.Playback();
        Entity first = created[0].Resolve(), second = created[1].Resolve();
        Require(world.ReadValue<RelationEndpoints<Link>>(first).Target == second
            && world.ReadValue<RelationEndpoints<Link>>(second).Source == first,
            "captured creates publish mutually referenced edge identities");
    }

    private static void EndpointFailureKeepsTheCompletedPrefix()
    {
        using var world = new World();
        Entity a = world.Create(), b = world.Create(), c = world.Create(), d = world.Create();
        Entity edge = world.CreateRelation(a, b, new Link()).Entity;
        using var commands = new CommandBuffer(world);
        commands.EditEndpoints<Link>([new EndpointCommandEdit(edge, a, c)]);
        commands.EditEndpoints<Link>([new CapturedEndpointEdit(edge, a, b, a, d)], Span<DeferredEntity>.Empty,
            world.DataGeneration);
        bool failed = false;
        try { commands.Playback(); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && commands.CompletedCommands == 1 && world.ReadValue<RelationEndpoints<Link>>(edge).Target == c,
            "changed endpoints reject the conditional composite after its visible prefix");
    }

    private static void Require(bool value, string behaviour)
    {
        if (!value) throw new InvalidOperationException(behaviour);
    }
}
