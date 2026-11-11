namespace SomeEngine.ECS.Comparison;

internal static class NativeValueChecks
{
    internal static void Run()
    {
        ContinuousValuesReturnAfterTailRemoval();
        MiddleGapsKeepChunkRanges();
        GrowingEarlierLayoutUpdatesCachedColumns();
        using var world = new World(64);
        using (var chunks = new World(24, chunkCapacity: 8))
        {
            chunks.Reserve<Position, Velocity, Health>(24);
            var rows = new SomeEngine.ECS.Entities.Entity[24];
            for (int row = 0; row < rows.Length; row++) rows[row] = chunks.Create(new Position { X = row }, new Velocity { X = 1 }, new Health { Value = 7 });
            Query ranges = chunks.Query<Position, Velocity, Health>();
            Require(ranges.TryGetValues<Position, Velocity, Health>(out _, out _, out _), "filled chunks bind shared columns");
            chunks.Add(rows[0], new Extra());
            Require(!ranges.TryGetValues<Position, Velocity, Health>(out _, out _, out _), "partial chunks retain individual ranges");
            var action = new CountRanges();
            ranges.ForEach<Position, Velocity, Health, CountRanges>(ref action);
            Require(action.Rows == 24 && action.Total == 276 && action.Maximum <= 8
                && chunks.Read<Position>(rows[0]).Y == 8 && chunks.Read<Position>(rows[23]).Y == 8,
                "bound column ranges preserve chunk limits, membership and writes after layout changes");
        }
        using (var selection = new World(32))
        {
            selection.Create(new Health(), new Team());
            selection.Create(new Position());
            Query selected = selection.Query(new QueryDescription().With<Health>().Without<Extra>());
            Require(selected.TryGetValues<Health>(out var selectedValues) && selectedValues.Length == 1,
                "one matching chunk binds current values");
            selection.Create(new Health(), new Team(), new GrowthMarker());
            Require(!selected.TryGetValues<Health>(out _) && selected.Count == 2,
                "new matching archetypes refresh the bound selection");
        }
        using (var tags = new World())
        {
            tags.Create(new Team());
            Query team = tags.Query<Team>();
            Require(!team.TryGetValues<Team>(out _) && team.Count == 1, "tag queries expose membership through the query");
        }
        world.Reserve<Position, Velocity, Health>(32);
        var entities = new SomeEngine.ECS.Entities.Entity[32];
        world.Create(entities, new Position(), new Velocity(), new Health { Value = 10, Max = 20 });
        Query pairs = world.Query<Position, Velocity>();
        Require(pairs.TryGetValues<Position, Velocity>(out var pairPositions, out var pairVelocities)
            && pairPositions.Length == 32 && pairVelocities.Length == 32, "two columns share complete row ranges");
        Query columns = world.Query<Position, Velocity, Health>();
        Require(columns.TryGetValues<Position, Velocity, Health>(out var positions, out var velocities, out var healthValues)
            && positions.Length == 32 && velocities.Length == 32 && healthValues.Length == 32
            && healthValues[31].Value == 10, "required columns share complete row ranges");
        Span<SomeEngine.ECS.Entities.Entity> other = stackalloc SomeEngine.ECS.Entities.Entity[16];
        for (int i = 0; i < other.Length; i++) other[i] = world.Create(new Position(), new Velocity());
        Require(pairs.Count == 48 && pairs.TryGetValues<Position, Velocity>(out pairPositions, out pairVelocities)
            && pairPositions.Length == 48 && pairVelocities.Length == 48,
            "new archetypes extend the bound column selection");
        Require(columns.Count == 32
            && columns.TryGetValues<Position, Velocity, Health>(out positions, out velocities, out healthValues)
            && positions.Length == 32 && velocities.Length == 32 && healthValues.Length == 32,
            "new archetypes refresh the bound column selection");
        world.Destroy(other);
        world.Create(new Extra());
        Query health = world.Query<Health>();
        Require(health.TryGetValues<Health>(out var values) && values.Length == 32, "filled type spans in mixed worlds");
        values[0].Max = 21;
        Require(world.Read<Health>(entities[0]).Max == 21, "view writes reach primary values");
        world.Destroy(entities[31]);
        Require(health.TryGetValues<Health>(out values) && values.Length == 31 && health.Count == 31, "destroy refreshes the visible row count");
        world.Create(new Position(), new Velocity(), new Health());
        Require(health.TryGetValues<Health>(out values) && values.Length == 32, "refill makes ranges complete");
        world.Remove<Health>(entities[0]);
        Require(!health.TryGetValues<Health>(out _) && health.Count == 31, "presence changes invalidate filled ranges");
        world.Clear();
        Require(health.Count == 0 && !health.Any(), "clear updates reused queries");
        Console.WriteLine("Native value views: tags, mixed worlds, primary writes, destroy/refill, presence and clear passed.");
    }

    private static void ContinuousValuesReturnAfterTailRemoval()
    {
        using var world = new World(16, chunkCapacity: 4);
        world.Reserve<Position, Velocity, Health>(8);
        var rows = new SomeEngine.ECS.Entities.Entity[7];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = world.Create(new Position { X = row }, new Velocity { X = 1 }, new Health { Value = 7 });
        var tail = world.Create(new Position { X = 99 }, new Velocity { X = 2 }, new Health { Value = 9 }, new Extra());
        Query values = world.Query<Position, Velocity, Health>();
        Require(!values.TryGetValues<Position, Velocity, Health>(out _, out _, out _),
            "separated live rows use individual ranges");
        world.Destroy(tail);
        Require(values.TryGetValues<Position, Velocity, Health>(out var positions, out var velocities, out var health)
            && positions.Length == 7 && velocities.Length == 7 && health.Length == 7
            && positions[6].X == 6 && velocities[6].X == 1 && health[6].Value == 7,
            "removing trailing rows restores continuous views over the current values");
    }

    private static void MiddleGapsKeepChunkRanges()
    {
        using var world = new World(12, chunkCapacity: 4);
        world.Reserve<Position, Velocity, Health>(12);
        var rows = new SomeEngine.ECS.Entities.Entity[12];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = world.Create(new Position { X = row }, new Velocity { X = 1 }, new Health { Value = 7 });
        Query values = world.Query<Position, Velocity, Health>();
        var removed = new SomeEngine.ECS.Entities.Entity[4];
        SomeEngine.ECS.Entities.Entity first, last;
        long expectedTotal = 0;
        {
            Require(values.TryGetValues<Position, Velocity, Health>(out var positions, out _, out _),
                "complete values expose continuous views");
            foreach (ref readonly Position position in positions) expectedTotal += (int)position.X;
            Span<Position> middle = positions.Slice((positions.Length - removed.Length) / 2, removed.Length);
            for (int row = 0; row < middle.Length; row++)
            {
                int ordinal = (int)middle[row].X;
                removed[row] = rows[ordinal];
                expectedTotal -= ordinal;
            }
            first = rows[(int)positions[0].X];
            last = rows[(int)positions[^1].X];
        }
        world.Destroy(removed);
        Require(!values.TryGetValues<Position, Velocity, Health>(out _, out _, out _) && values.Count == 8,
            "a gap between live rows retains individual ranges");
        var action = new CountRanges();
        values.ForEach<Position, Velocity, Health, CountRanges>(ref action);
        Require(action.Rows == 8 && action.Total == expectedTotal && action.Maximum <= 4
            && world.Read<Position>(first).Y == 8 && world.Read<Position>(last).Y == 8,
            "individual ranges preserve the live values and writes around the gap");
    }

    private static void GrowingEarlierLayoutUpdatesCachedColumns()
    {
        const int groupCount = 16;
        using var world = new World(groupCount * 2);
        var entities = new SomeEngine.ECS.Entities.Entity[groupCount * 2];
        var expected = new SomeCopyNode[entities.Length];
        for (int row = 0; row < entities.Length; row++)
        {
            var position = new Position { X = row, Y = row + 100 };
            entities[row] = row < groupCount
                ? world.Create(position, new SomeCopyNode(), new Team())
                : world.Create(position, new SomeCopyNode(), new Sleeping());
        }
        for (int row = 0; row < entities.Length; row++)
        {
            expected[row] = new(entities[row], entities[(row + 1) % entities.Length],
                entities[(row + groupCount) % entities.Length], row, row * 3 + 7);
            world.Set(entities[row], expected[row]);
        }
        using (var positions = world.OpenCachedRead<Position>())
        using (var nodes = world.OpenCachedRead<SomeCopyNode>())
            Require(positions.Has(entities[groupCount]) && nodes.Has(entities[groupCount]),
                "cached columns include the later layout before growth");
        Query values = world.Query<Position, SomeCopyNode>();
        _ = values.TryGetValues<Position, SomeCopyNode>(out _, out _);
        world.Reserve<Position, SomeCopyNode, Team>(128);
        using (var positions = world.OpenCachedRead<Position>())
        using (var nodes = world.OpenCachedRead<SomeCopyNode>())
            for (int row = groupCount; row < entities.Length; row++)
            {
                Position position = positions.Read(entities[row]);
                Require(position.X == row && position.Y == row + 100 && nodes.Read(entities[row]) == expected[row],
                    "cached reads retain each later entity's complete values after earlier layout growth");
            }
        var action = new MatchNodeRows(expected);
        values.ForEach<Position, SomeCopyNode, MatchNodeRows>(ref action);
        Require(action.Rows == entities.Length && action.OrdinalBits == (1UL << entities.Length) - 1,
            "two-column ranges retain all corresponding rows after earlier layout growth");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct MatchNodeRows(SomeCopyNode[] expected) : IComponentAction<Position, SomeCopyNode>
    {
        internal int Rows;
        internal ulong OrdinalBits;
        public void Execute(Span<Position> positions, ReadOnlySpan<SomeCopyNode> nodes)
        {
            for (int row = 0; row < positions.Length; row++)
            {
                int ordinal = (int)positions[row].X;
                Require(positions[row].Y == ordinal + 100 && nodes[row] == expected[ordinal],
                    "two-column callbacks pair each position with its complete node value");
                Rows++;
                OrdinalBits |= 1UL << ordinal;
            }
        }
    }

    private struct CountRanges : IComponentAction<Position, Velocity, Health>
    {
        internal int Rows, Maximum;
        internal long Total;
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
        {
            Rows += positions.Length;
            Maximum = Math.Max(Maximum, positions.Length);
            for (int row = 0; row < positions.Length; row++)
            {
                Total += (int)positions[row].X;
                positions[row].Y = velocities[row].X + health[row].Value;
            }
        }
    }
}
