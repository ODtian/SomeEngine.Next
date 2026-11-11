using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Comparison;

internal static class HierarchyChecks
{
    private readonly struct Scene : IHierarchyDomain;
    private readonly struct Ui : IHierarchyDomain;

    internal static void Run()
    {
        using var world = new World(4, chunkCapacity: 8);
        Entity first = world.Create(new Position { X = 1 });
        Entity second = world.Create(new Position { X = 2 });
        Entity child = world.Create(new Position { X = 3 });
        Entity sibling = world.Create(new Position { X = 4 });
        Entity leaf = world.Create(new Position { X = 5 });
        ulong before = world.Version;
        Hierarchy<Scene>.SetParent(world, child, first);
        Require(world.Query(new QueryDescription().WithAdded<Parent<Scene>>(before)).Count == 1
            && world.Query(new QueryDescription().WithAdded<Children<Scene>>(before)).Count == 1,
            "parent and first children use native Added facts");
        Hierarchy<Scene>.SetParent(world, sibling, first);
        Hierarchy<Scene>.SetParent(world, leaf, child);
        var descendants = new List<Entity>();
        foreach (Entity entity in Hierarchy<Scene>.GetDescendants(world, first)) descendants.Add(entity);
        Require(descendants.SequenceEqual(new[] { child, leaf, sibling }), "descendants visit applied children in depth-first local order");
        before = world.Version;
        Hierarchy<Scene>.SetParent(world, child, second);
        Require(Hierarchy<Scene>.GetParent(world, child) == second
            && world.Query(new QueryDescription().WithChanged<Parent<Scene>>(before)).Count == 1,
            "immediate changes update the main parent and its native version");
        RequireChildren<Scene>(world, first, sibling);
        RequireChildren<Scene>(world, second, child);
        Hierarchy<Scene>.Detach(world, child);
        Require(world.Read<Removed<Parent<Scene>>>(child).Value.Value == second
            && world.Read<Removed<Children<Scene>>>(second).Value.Entities.SequenceEqual(new[] { child }),
            "detach preserves the old parent and last children contents");
        Hierarchy<Scene>.SetParentDeferred(world, leaf, second);
        RequireChildren<Scene>(world, child, leaf);
        Require(Hierarchy<Scene>.GetParent(world, leaf) == second, "deferred edits publish their main value");
        Hierarchy<Scene>.SetParent(world, sibling, first);
        Hierarchy<Scene>.Maintain(world);
        RequireChildren<Scene>(world, child);
        RequireChildren<Scene>(world, second, leaf);
        world.Destroy(second);
        Require(Hierarchy<Scene>.GetParent(world, leaf) == Entity.Null && world.IsPendingCleanup(second),
            "ordinary destroy orphans direct children and retains cleanup facts");

        Hierarchy<Ui>.SetParent(world, leaf, first);
        Require(Hierarchy<Scene>.GetParent(world, leaf) == Entity.Null && Hierarchy<Ui>.GetParent(world, leaf) == first,
            "typed domains hold independent parent links");
        Entity initialized = world.Create(new Position { X = 6 }, new Parent<Scene>(first));
        Entity copy = world.Clone(initialized);
        Require(Hierarchy<Scene>.GetParent(world, copy) == first, "typed creation and clone initialize parent links");
        Entity rootCopy = world.Clone(first);
        Require(!world.Has<Children<Scene>>(rootCopy) && !world.Has<Children<Ui>>(rootCopy),
            "cloning a parent rebuilds its computed children");
        Reject(() => world.Set(child, new Parent<Scene>(first)));
        Reject(() => world.Remove<Parent<Scene>>(initialized));

        world.DestroyDeferred(first);
        ClearFacts<Scene>(world); ClearFacts<Ui>(world);
        Entity reused = world.Create(new Position { X = 7 });
        Hierarchy<Scene>.Maintain(world); Hierarchy<Ui>.Maintain(world);
        Require(Hierarchy<Scene>.GetParent(world, sibling) == Entity.Null
            && Hierarchy<Scene>.GetParent(world, initialized) == Entity.Null
            && Hierarchy<Scene>.GetParent(world, copy) == Entity.Null
            && Hierarchy<Ui>.GetParent(world, leaf) == Entity.Null && world.IsAlive(reused),
            "maintenance uses retired full identities after fact cleanup and reuse");

        world.Clear();
        Entity root = world.Create(new Position());
        Entity a = world.Create(new Position());
        Entity b = world.Create(new Position());
        Entity c = world.Create(new Position());
        Hierarchy<Scene>.SetOrdered(world, root, true);
        Hierarchy<Scene>.SetParentDeferred(world, a, root, 0);
        Hierarchy<Scene>.SetParentDeferred(world, b, root, 0);
        Hierarchy<Scene>.SetParentDeferred(world, c, root, 1);
        RequireChildren<Scene>(world, root);
        Hierarchy<Scene>.Maintain(world);
        RequireChildren<Scene>(world, root, b, c, a);
        Hierarchy<Scene>.SetParent(world, a, root, 0);
        RequireChildren<Scene>(world, root, a, b, c);
        Reject(() => Hierarchy<Scene>.SetParents(world, new[] { new ParentEdit(root, a), new ParentEdit(a, root) }));
        Require(Hierarchy<Scene>.GetParent(world, root) == Entity.Null, "final forest validation precedes batch publication");
        Hierarchy<Scene>.DestroySubtree(world, root);
        ClearFacts<Scene>(world);
        Hierarchy<Scene>.Maintain(world);
        Require(world.EntityCount == 0, "subtree destruction completes every selected lifetime");
        CheckBatch();
        Console.WriteLine("Hierarchy native facts, deferred maintenance, domain independence, copy, order and lifetime behavior: passed.");
    }

    private static void CheckBatch()
    {
        using var world = new World(4, chunkCapacity: 4);
        Entity first = world.Create(new Position());
        Entity second = world.Create(new Position());
        Entity a = world.Create(new Position());
        Entity b = world.Create(new Position());
        Entity c = world.Create(new Position());
        Hierarchy<Scene>.SetParent(world, a, first);
        Hierarchy<Scene>.SetParent(world, b, second);
        ulong before = world.Version;
        Hierarchy<Scene>.SetParents(world, new[] { new ParentEdit(a, second), new ParentEdit(b, first) });
        RequireChildren<Scene>(world, first, b);
        RequireChildren<Scene>(world, second, a);
        Require(world.Version == before + 1
            && world.Query<Removed<Children<Scene>>>().Count == 0
            && world.Query(new QueryDescription().WithAdded<Children<Scene>>(before)).Count == 0
            && world.Query(new QueryDescription().WithChanged<Children<Scene>>(before)).Count == 2,
            "a batch exchanges children with one version and the final component lifetimes");
        Hierarchy<Scene>.SetParents(world, new[] { new ParentEdit(b, second), new ParentEdit(a, Entity.Null) });
        Require(world.Read<Removed<Children<Scene>>>(first).Value.Entities.SequenceEqual(new[] { b }),
            "a final empty parent preserves its old children");
        Hierarchy<Scene>.SetParent(world, c, second);
        Hierarchy<Scene>.SetParents(world, new[] { new ParentEdit(b, Entity.Null), new ParentEdit(c, Entity.Null) });
        Require(world.Read<Removed<Children<Scene>>>(second).Value.Entities.SequenceEqual(new[] { b, c }),
            "batch removal preserves the whole previous children value");
        Hierarchy<Scene>.SetOrdered(world, first, true);
        Hierarchy<Scene>.SetParents(world, new[] { new ParentEdit(a, first, 0), new ParentEdit(b, first, 1), new ParentEdit(c, first, 1) });
        RequireChildren<Scene>(world, first, a, c, b);
    }

    private static void ClearFacts<T>(World world) where T : struct, IHierarchyDomain
    {
        world.ClearRemoved<Parent<T>>(); world.ClearRemoved<Children<T>>();
    }

    private static void RequireChildren<T>(World world, Entity parent, params Entity[] expected) where T : struct, IHierarchyDomain
    {
        using var children = Hierarchy<T>.GetChildren(world, parent);
        Require(children.Entities.SequenceEqual(expected), "children preserve the applied members and local order");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Controlled mutations preserve their public entry rules.");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
}
