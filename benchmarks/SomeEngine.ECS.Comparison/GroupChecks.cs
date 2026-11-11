using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class GroupChecks
{
    internal static void Run(int chunkCapacity = 0)
    {
        using var world = new World(chunkCapacity: chunkCapacity);
        var entities = new Entity[200];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new Position { X = i }, new Zone { Group = i % 16, Ordinal = i });
        var seventh = world.Query(new QueryDescription().InGroup<Zone>(7));
        using (var positions = world.OpenCachedRead<Position>()) Require(positions.Read(entities[7]).X == 7, "bind positions before grouping");
        Zone.Groups = 0;
        world.GroupBy<Zone>();
        Require(Zone.Groups == entities.Length && seventh.Count == 13, "activation projects every main value once and refreshes existing queries");
        var index = world.Index<Zone, int>();
        Zone.Groups = Zone.Keys = 0;
        world.Set(entities[7], new Zone { Group = -1, Ordinal = 7 });
        Require(Zone.Groups == 1 && Zone.Keys == 1 && seventh.Count == 12 && Count(index, -1) == 1,
            "one write updates the group and value index with one projection each");
        ulong beforeLayers = world.Version;
        world.GroupBy<Layer>();
        Zone.Groups = 0;
        for (int i = 0; i < entities.Length; i++) world.Set(entities[i], new Layer { Group = i % 2 });
        Require(Zone.Groups == 0 && world.Query(new QueryDescription().WithChanged<Zone>(beforeLayers)).Count == 0,
            "adding a grouping dimension preserves existing group keys and write versions");
        var combined = world.Query(new QueryDescription().InGroup<Zone>(7).InGroup<Layer>(1));
        Require(combined.Count == 12, "multiple group dimensions select their intersection");
        world.Set(entities[0], new Zone { Group = 7, Ordinal = 0 });
        Require(world.Query(new QueryDescription().InGroup<Zone>(7).InGroup<Layer>(0)).Count == 1 && Count(index, 7) == 13,
            "a changed group preserves the other grouping dimension");
        world.Remove<Zone>(entities[23]);
        Require(world.Read<Removed<Zone>>(entities[23]).Value.Group == 7 && combined.Count == 11,
            "removal leaves the group query and preserves the old component value");
        ulong beforeAdd = world.Version;
        world.Set(entities[23], new Zone { Group = 7, Ordinal = 23 });
        Require(world.Query(new QueryDescription().WithAdded<Zone>(beforeAdd)).Count == 1 && combined.Count == 12,
            "adding a grouped component records its Added fact");
        Entity created = world.Create(new Position { X = 999 }, new Zone { Group = 3, Ordinal = 999 }, new Layer { Group = 5 });
        Require(world.Query(new QueryDescription().InGroup<Zone>(3).InGroup<Layer>(5)).Count == 1,
            "typed creation publishes the final grouped layout");
        Entity copy = world.Clone(entities[39]);
        using (var positions = world.OpenCachedRead<Position>())
            Require(positions.Read(copy).X == 39 && positions.Read(entities[7]).X == 7, "group moves and clone maintain bound positions");
        world.Disable<Zone>(entities[39]);
        Zone.Keys = 0;
        world.Set(entities[39], new Zone { Group = 2, Ordinal = 39 });
        Require(Zone.Keys == 0 && world.Query(new QueryDescription().InGroup<Zone>(2).WithDisabled<Zone>()).Count == 1,
            "disabled writes update the stored group while preserving index activation state");
        world.Enable<Zone>(entities[39]);
        Require(Zone.Keys == 1 && world.Query(new QueryDescription().InGroup<Zone>(2).InGroup<Layer>(1)).Count == 1,
            "enable publishes the current grouped value to the index");
        using (var scope = world.Query(new QueryDescription().With<Zone>().With<Layer>()).Open())
            foreach (var rows in scope)
            {
                int zone = rows.Group<Zone>(), layer = rows.Group<Layer>();
                var zones = rows.Read<Zone>();
                var layers = rows.Read<Layer>();
                var positions = rows.Read<Position>();
                for (int row = 0; row < rows.Count; row++)
                    Require(zones[row].Group == zone && layers[row].Group == layer && zones[row].Ordinal == (int)positions[row].X,
                        "each returned contiguous range carries one group tuple and aligned main values");
            }
        world.Clear();
        world.Create(new Position { X = 1 }, new Zone { Group = 7, Ordinal = 1 }, new Layer { Group = 1 });
        Require(combined.Count == 1 && Count(index, 7) == 1, "clear reuses grouping and index registrations");
        var batch = new Entity[10];
        world.Create(batch, new Position { X = 42 }, new Zone { Group = 3, Ordinal = 42 }, new Layer { Group = 2 });
        Require(world.Query(new QueryDescription().InGroup<Zone>(3).InGroup<Layer>(2)).Count == 10,
            "batch creation publishes every grouped row");
        Console.WriteLine("GroupBy activation, contiguous tuples, value indexes, versions, clone and lifecycle behavior: passed.");
    }

    private static int Count(ValueIndex<Zone, int> index, int key)
    {
        using var members = index.Find(key);
        return members.Count;
    }
    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    private struct Zone : IGroupComponent, IIndexedComponent<int>, IAddedComponent, IRemovedComponent, IEnableableComponent
    {
        internal static int Groups, Keys;
        internal int Group, Ordinal;
        public readonly int GetGroup() { Groups++; return Group; }
        public readonly int GetKey() { Keys++; return Group; }
    }
    private struct Layer : IGroupComponent
    {
        internal int Group;
        public readonly int GetGroup() => Group;
    }
}
