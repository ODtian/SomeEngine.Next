using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal static class CopyChecks
{
    internal static void Run()
    {
        using var source = new World();
        source.Index<CopiedNode, int>(); source.GroupBy<SharedLabel>();
        Entity root = source.Create(new Position { X = 10 }), child = source.Create(new Position { X = 20 });
        Entity outside = source.Create(new Position { X = 30 });
        source.Set(root, new CopiedNode(root, child, outside, 7));
        source.Set(child, new CopiedNode(child, root, outside, 11));
        source.SetShared(child, new SharedLabel(7));
        source.Add(child, new CopiedSignal { Value = 5 }); source.Disable<CopiedSignal>(child);
        source.AddBuffer<LinkedValue>(child);
        using (var values = source.GetBuffer<LinkedValue>(child)) { values.Add(new(root, 1)); values.Add(new(outside, 2)); }
        source.Set(child, new LinkedValue(root, 3));
        Hierarchy<CopyTree>.SetParent(source, child, root);
        var edge = source.CreateRelation(root, child, new CopiedLink(17));
        Entity[] selection = [root, child, edge.Entity], copied = new Entity[3];
        source.Clone(selection, copied);
        CheckGraph(source, copied, outside);
        using (var values = source.GetBuffer<LinkedValue>(copied[1])) values[0] = new(copied[1], 9);
        using (var values = source.ReadBuffer<LinkedValue>(child)) Require(values[0].Entity == root && values[0].Value == 1,
            "clone buffer elements have independent storage");
        Entity scalar = source.Clone(root);
        Require(source.Read<CopiedNode>(scalar).Self == scalar && source.Read<CopiedNode>(scalar).Inside == child,
            "scalar clone remaps self references and retains references outside its copy set");
        using var target = new World();
        target.Index<CopiedNode, int>(); target.GroupBy<SharedLabel>();
        Entity targetOutside = target.Create(new Position { X = 30 });
        var external = new ExternalEntities(outside, targetOutside);
        source.CloneTo(target, selection, copied, ref external);
        CheckGraph(target, copied, targetOutside);
        using var prefab = new EntityPrefab(source, selection);
        source.Clear();
        prefab.Instantiate(target, copied, ref external);
        CheckGraph(target, copied, targetOutside);
        target.Clear();
        targetOutside = target.Create(new Position { X = 30 });
        external = new(outside, targetOutside);
        prefab.Instantiate(target, copied, ref external);
        CheckGraph(target, copied, targetOutside);
        using (var found = target.Index<CopiedNode, int>().Find(7))
            Require(found.Count == 1, "copied main values reach their value index before the copy returns");
        Console.WriteLine("Clone and Prefab: internal/external references, independent buffers, sparse/shared/enabled values, native topology, target indexes and Clear bindings passed.");
    }

    private static void CheckGraph(World world, Entity[] copied, Entity outside)
    {
        CopiedNode root = world.Read<CopiedNode>(copied[0]), child = world.Read<CopiedNode>(copied[1]);
        Require(root.Self == copied[0] && root.Inside == copied[1] && root.Outside == outside
            && child.Self == copied[1] && child.Inside == copied[0] && child.Outside == outside,
            "copy maps references inside the complete set and applies the external reference rule");
        using (var values = world.ReadBuffer<LinkedValue>(copied[1]))
            Require(values.Count == 2 && values[0] == new LinkedValue(copied[0], 1) && values[1] == new LinkedValue(outside, 2),
                "buffer element references use the same complete copy map");
        Require(world.Read<LinkedValue>(copied[1]) == new LinkedValue(copied[0], 3)
            && world.ReadShared<SharedLabel>(copied[1]).Value == 7 && !world.IsEnabled<CopiedSignal>(copied[1]),
            "sparse references, shared values and disabled component values retain their copy semantics");
        Require(Hierarchy<CopyTree>.GetParent(world, copied[1]) == copied[0]
            && world.Read<RelationEndpoints<CopiedLink>>(copied[2]) == new RelationEndpoints<CopiedLink>(copied[0], copied[1])
            && world.Read<CopiedLink>(copied[2]).Value == 17, "copied parents and endpoints rebuild their native topology from remapped main values");
        using (var children = Hierarchy<CopyTree>.GetChildren(world, copied[0])) Require(children.Count == 1 && children.Entities[0] == copied[1],
            "copied children contain the new child identity");
        using (var outgoing = world.GetOutgoingRelations<CopiedLink>(copied[0])) Require(outgoing.Count == 1 && outgoing.Entries[0].Edge == copied[2],
            "copied adjacency contains the new independent edge identity");
    }

    private readonly record struct CopiedNode(Entity Self, Entity Inside, Entity Outside, int Key)
        : IIndexedComponent<int>, IEntityReferenceValue<CopiedNode>
    {
        public int GetKey() => Key;
        public static void Remap(ref CopiedNode value, in EntityMap entities)
            => value = value with { Self = entities.Map(value.Self), Inside = entities.Map(value.Inside), Outside = entities.Map(value.Outside) };
    }
    private readonly record struct LinkedValue(Entity Entity, long Value) : IEntityReferenceValue<LinkedValue>
    {
        public static void Remap(ref LinkedValue value, in EntityMap entities) => value = value with { Entity = entities.Map(value.Entity) };
    }
    private readonly record struct SharedLabel(int Value) : ISharedComponent, IGroupComponent { public int GetGroup() => Value; }
    private struct CopiedSignal : IEnableableComponent { internal int Value; }
    private readonly struct CopyTree : IHierarchyDomain;
    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private readonly record struct CopiedLink(long Value) : IComponent;
    private readonly struct ExternalEntities(Entity source, Entity target) : IEntityMapper
    {
        public Entity Map(Entity entity) => entity == source ? target : entity;
    }

}
