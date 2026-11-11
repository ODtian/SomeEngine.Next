using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class GenericQueryExecutionTests
{
    [Fact]
    public void OptionalReadKeepsMembersAndReadsManagedComponentsByReference()
    {
        using var world = new World();
        Entity named = world.Create(new Position { X = 1 });
        world.Add(named, new DisplayName { Value = "Iris" });
        world.Create(new Position { X = 2 });
        world.Create(new DisplayName { Value = "Other" });
        Query query = world.Query<Position>().WithOptionalRead<DisplayName>();
        Span<int> lengths = stackalloc int[2];
        int index = 0;

        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    lengths[index++] = chunk.Has<DisplayName>(row) ? chunk.Read<DisplayName>()[row].Value.Length : -1;

        lengths.Sort();
        Assert.Equal([-1, 4], lengths.ToArray());
    }

    [Fact]
    public void TypedRowReadsRetainOptionalComponentsAndOriginalMembers()
    {
        using var world = new World();
        Entity named = world.Create(new Position { X = 1 });
        world.Add(named, new DisplayName { Value = "Iris" });
        world.Create(new Position { X = 2 });
        world.Create(new DisplayName { Value = "Other" });
        Query query = world.Query(new QueryDescription().WithStored<Position>().Optional<Position>().Optional<DisplayName>());
        Span<int> values = stackalloc int[2];
        int index = 0;

        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                {
                    OptionalComponent<DisplayName> name = chunk.Optional<DisplayName>(row);
                    values[index++] = (int)chunk.Read<Position>()[row].X + (name.IsPresent ? name.Value.Value.Length : 0);
                }

        values.Sort();
        Assert.Equal([2, 5], values.ToArray());
    }

    [Fact]
    public void RequiredComponentsFollowTheExecutingMemberQuery()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        Query required = world.Query(new QueryDescription().WithStored<Position>().Optional<DisplayName>());
        Query any = world.Query(new QueryDescription().WithAny<Position>().Optional<DisplayName>());

        using (QueryScope scope = required.OpenRead())
        {
            Assert.True(scope.Contains(entity));
            foreach (QueryChunk chunk in scope)
            {
                Assert.True(chunk.Has<Position>());
                Assert.False(chunk.Has<DisplayName>());
            }
        }
        using QueryScope anyScope = any.OpenRead();
        Assert.True(anyScope.Contains(entity));
        foreach (QueryChunk chunk in anyScope)
        {
            Assert.True(chunk.Has<Position>());
            Assert.False(chunk.Has<DisplayName>());
        }
    }

    [Fact]
    public void MemberChecksFollowChangedRowsAndOptionalReads()
    {
        using var world = new World();
        Entity changed = world.Create(new Position());
        Entity unchanged = world.Create(new Position());
        Entity other = world.Create(new DisplayName { Value = "Iris" });
        Query query = world.Query(new QueryDescription().WithChanged<Position>(0).Optional<DisplayName>());
        ulong baseline = world.Version;
        world.Set(changed, new Position { X = 1 });
        var members = new List<Entity>();

        using (QueryScope scope = query.Since(baseline).OpenRead())
            foreach (Entity entity in new[] { changed, unchanged, other })
                if (scope.Contains(entity)) members.Add(entity);

        Assert.Equal([changed], members);
    }

    [Fact]
    public void PointReadsBorrowOptionalManagedComponentsFromChangedMembers()
    {
        using var world = new World();
        Entity changed = world.Create(new Position());
        world.Add(changed, new DisplayName { Value = "Iris" });
        Entity missing = world.Create(new Position());
        Entity unchanged = world.Create(new Position());
        world.Add(unchanged, new DisplayName { Value = "Rose" });
        Entity other = world.Create(new DisplayName { Value = "Other" });
        Query query = world.Query(new QueryDescription().WithChanged<Position>(0).Optional<DisplayName>());
        ulong baseline = world.Version;
        world.Set(changed, new Position { X = 1 });
        world.Set(missing, new Position { X = 2 });
        Entity[] entities = [changed, missing, unchanged, other];
        Span<int> lengths = stackalloc int[4];

        using (QueryScope scope = query.Since(baseline).OpenRead())
        using (ReadLookup<DisplayName> names = world.OpenRead<DisplayName>())
            for (int index = 0; index < entities.Length; index++)
                lengths[index] = scope.Contains(entities[index]) && names.Has(entities[index])
                    ? names.Read(entities[index]).Value.Length : -1;

        Assert.Equal([4, -1, -1, -1], lengths.ToArray());
    }

    [Fact]
    public void ChunkChangeReadsKeepOptionalRowsAndTheirWriteVersions()
    {
        using var world = new World();
        Entity changed = world.Create(new Position());
        world.Add(changed, new DisplayName { Value = "Iris" });
        Entity unchanged = world.Create(new Position());
        world.Add(unchanged, new DisplayName { Value = "Rose" });
        world.Create(new Position());
        Query query = world.Query(new QueryDescription().WithStored<Position>().Optional<DisplayName>());
        ulong baseline = world.Version;
        world.Set(changed, new DisplayName { Value = "Lily" });
        var entities = new List<Entity>();

        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.WasWritten<DisplayName>(row, baseline)) entities.Add(chunk.Entities[row]);

        Assert.Equal([changed], entities);
    }

    [Fact]
    public void OptionalReadsRetainAnyAndExcludedMemberConditions()
    {
        using var world = new World();
        Entity position = world.Create(new Position());
        Entity velocity = world.Create(new Velocity());
        Entity excluded = world.Create(new Position());
        world.Add(excluded, new DisplayName { Value = "Iris" });
        Query query = world.Query(new QueryDescription().WithAny<Position>().WithAny<Velocity>()
            .Without<DisplayName>().Optional<Velocity>().Optional<DisplayName>());
        var members = new List<Entity>();

        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope) members.AddRange(chunk.Entities);

        Assert.Equal([position, velocity], members);
    }

    [Fact]
    public void ScopedManagedReadsLeaveTheWorldReadyForFollowingStructureChanges()
    {
        using var world = new World();
        Entity entity = world.Create(new DisplayName { Value = "Iris" });
        Query query = world.Query<DisplayName>();
        int total = 0;
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (ref readonly DisplayName name in chunk.Read<DisplayName>()) total += name.Value.Length;

        world.Add(entity, new Position { X = 7 });

        Assert.Equal(4, total);
        Assert.Equal(7, world.Read<Position>(entity).X);
    }

    private struct DisplayName : IAddedComponent
    {
        public string Value;
    }
}
