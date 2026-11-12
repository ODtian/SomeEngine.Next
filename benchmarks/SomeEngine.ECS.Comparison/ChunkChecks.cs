using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class ChunkChecks
{
    internal static void Run()
    {
        using var world = new World(8, chunkCapacity: 32);
        Query query = world.Query(new QueryDescription().With<Position>().With<Velocity>().With<Health>());
        Require(query.Count == 0, "an existing query starts with its current empty membership");
        var original = new Entity[80];
        for (int i = 0; i < original.Length; i++)
            original[i] = world.Create(new Position { X = i }, new Velocity { X = 2 }, new Health { Value = 7 });
        Require(query.Count == 80, "a query observes the initial fixed chunks");
        using (var cached = world.OpenCachedRead<Position>()) Require(cached.Read(original[63]).X == 63, "bind across initial chunks");

        Entity extra = world.Create(new Position(), new Velocity { X = 5 }, new Health { Value = 11 }, new Extra());
        var added = new Entity[96];
        world.Create(added, new Position(), new Velocity { X = 3 }, new Health { Value = 4 });
        Require(query.Count == 177, "an existing query sees a new table and new chunks in an existing table");
        var one = new ChangeHealth();
        var two = new Move();
        var three = new Calculate();
        query.ForEach<Health, ChangeHealth>(ref one);
        query.ForEach<Position, Velocity, Move>(ref two);
        query.ForEach<Position, Velocity, Health, Calculate>(ref three);
        int rowsSeen = 0, ranges = 0;
        long total = 0;
        using (var scope = query.Open())
            foreach (var rows in scope)
            {
                Require(rows.Count <= 32, "each returned range respects its fixed chunk capacity");
                ranges++;
                rowsSeen += rows.Count;
                for (int i = 0; i < rows.Count; i++)
                {
                    float expected = rows.Read<Velocity>()[i].X + rows.Read<Health>()[i].Value;
                    Require(rows.Read<Position>()[i].X == expected, "typed callbacks reach every chunk and preserve column alignment");
                    total += (int)rows.Read<Position>()[i].X;
                }
            }
        Require(rowsSeen == 177 && ranges == 7 && total == 1585, "all fixed ranges produce the requested row results");
        using (var cached = world.OpenCachedRead<Position>())
            Require(cached.Read(original[63]).X == 10 && cached.Read(added[^1]).X == 8 && cached.Read(extra).X == 17,
                "cached positions include newly appended chunks and tables");
        world.Destroy(added);
        Require(query.Count == 81, "batch reclamation removes its rows across chunks");
        var reused = new Entity[64];
        world.Create(reused, new Position { X = 9 }, new Velocity(), new Health());
        Require(query.Count == 145, "reclaimed chunk capacity accepts new rows");
        using (var cached = world.OpenCachedRead<Position>())
            Require(!cached.Has(added[0]) && !cached.Has(added[^1]) && cached.Read(reused[^1]).X == 9
                && cached.Read(original[0]).X == 10,
                "reclaimed chunks invalidate old bindings and retain current values after reuse");

        var releases = new ReleaseCount();
        var managed = new Entity[40];
        for (int i = 0; i < managed.Length; i++)
        {
            managed[i] = world.Create(new Position { X = i }, new OwnedValue(releases, i));
            world.AddBuffer<Element>(managed[i]);
            using var buffer = world.GetBuffer<Element>(managed[i]);
            buffer.Add(new Element { Value = i });
        }
        world.Remove<OwnedValue>(managed[0]);
        Entity absentCopy = world.Clone(managed[0]);
        Entity ownedCopy = world.Clone(managed[1]);
        Require(!world.Has<OwnedValue>(absentCopy) && world.Read<OwnedValue>(ownedCopy).Value == 1,
            "cross-chunk clone preserves component presence and managed values");
        using (var copy = world.GetBuffer<Element>(ownedCopy)) copy[0].Value = 1001;
        using (var source = world.ReadBuffer<Element>(managed[1]))
            Require(source[0].Value == 1, "cross-chunk clone owns its buffer independently");
        world.Clear();
        Require(query.Count == 0 && world.EntityCount == 0 && releases.Count == 41,
            "clear visits every chunk and releases every owned value once");
        Console.WriteLine("Fixed chunks, query refresh, typed callbacks, reclamation, clone and owned values: passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private struct ChangeHealth : IComponentAction<Health>
    {
        public void Execute(Span<Health> values) { foreach (ref var value in values) value.Value++; }
    }
    private struct Move : IComponentAction<Position, Velocity>
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
        {
            for (int i = 0; i < positions.Length; i++) positions[i].X += velocities[i].X;
        }
    }
    private struct Calculate : IComponentAction<Position, Velocity, Health>
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
        {
            for (int i = 0; i < positions.Length; i++) positions[i].X = velocities[i].X + health[i].Value;
        }
    }
    private struct Element : IComponent { internal int Value; }
    private sealed class ReleaseCount { internal int Count; }
    private readonly struct OwnedValue(ReleaseCount releases, int value) : IComponent, IDisposable
    {
        internal int Value => value;
        public void Dispose() => releases.Count++;
    }
}
