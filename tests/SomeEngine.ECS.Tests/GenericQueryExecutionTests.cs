using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class GenericQueryExecutionTests
{
    [Fact]
    public void OptionalReadKeepsMembersAndReadsManagedComponentsByReference()
    {
        using var world = new World();
        var named = world.CreateEntity(new Position { X = 1 });
        world.Add(named, new DisplayName { Value = "Iris" });
        world.CreateEntity(new Position { X = 2 });
        world.CreateEntity(new DisplayName { Value = "Other" });
        var members = world.Query(world.QueryDefinition().All<Position>());
        var query = world.Query(world.GetQueryDefinition(members).WithOptionalRead<DisplayName>());
        Span<int> lengths = stackalloc int[2];
        var execution = new NameLengths(lengths);

        world.ExecuteQuery(query, ref execution);

        lengths.Sort();
        Assert.Equal([-1, 4], lengths.ToArray());
    }

    [Fact]
    public void TypedRowReadsRetainOptionalComponentsAndOriginalMembers()
    {
        using var world = new World();
        var named = world.CreateEntity(new Position { X = 1 });
        world.Add(named, new DisplayName { Value = "Iris" });
        world.CreateEntity(new Position { X = 2 });
        world.CreateEntity(new DisplayName { Value = "Other" });
        var definition = world.QueryDefinition().All<Position>().Build()
            .WithOptionalRead<Position>().WithOptionalRead<DisplayName>();
        var query = world.Query(definition);
        Span<int> values = stackalloc int[2];
        var execution = new TypedNameLengths(values);

        world.ExecuteQuery(query, ref execution);

        values.Sort();
        Assert.Equal([2, 5], values.ToArray());
    }

    [Fact]
    public void ChunkChangeReadsKeepOptionalRowsAndTheirWriteVersions()
    {
        using var world = new World();
        var changed = world.CreateEntity(new Position());
        world.Add(changed, new DisplayName { Value = "Iris" });
        var unchanged = world.CreateEntity(new Position());
        world.Add(unchanged, new DisplayName { Value = "Rose" });
        world.CreateEntity(new Position());
        var query = world.Query(world.QueryDefinition().All<Position>().Build().WithOptionalRead<DisplayName>());
        uint baseline = world.AcquireSystemTick();
        world.Replace(changed, new DisplayName { Value = "Lily" });
        var entities = new List<Entity>();
        var execution = new ChangedNameRows(entities);

        world.ExecuteQuery(query, baseline, ref execution);

        Assert.Equal([changed], entities);
    }

    [Fact]
    public void OptionalReadsRetainAnyAndExcludedMemberConditions()
    {
        using var world = new World();
        world.CreateEntity(new Position());
        world.CreateEntity(new Velocity());
        var excluded = world.CreateEntity(new Position());
        world.Add(excluded, new DisplayName { Value = "Iris" });
        QueryDefinition definition = world.QueryDefinition()
            .Any<Position>().Any<Velocity>().None<DisplayName>().Build();
        var query = world.Query(definition.WithOptionalRead<Velocity>().WithOptionalRead<DisplayName>());
        var execution = new RowCount();

        world.ExecuteQuery(query, ref execution);

        Assert.Equal(2, execution.Count);
    }

    [Fact]
    public void ReadRefAccessFailureReleasesTheQueryScope()
    {
        using var world = new World();
        var entity = world.CreateEntity(new DisplayName { Value = "Iris" });
        var query = world.Query(world.QueryDefinition().All<DisplayName>());
        var execution = new NameTotal();

        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, ref execution));
        world.Add(entity, new Position { X = 7 });

        Assert.Equal(7, world.Read<Position>(entity).X);
    }

    private struct RowCount : IQueryExecution
    {
        public int Count;

        public void Execute(QueryCursor query)
        {
            foreach (var row in query.Rows)
                Count++;
        }
    }

    private struct NameTotal : IQueryExecution
    {
        public int Total;

        public void Execute(QueryCursor query)
        {
            foreach (var row in query.Rows)
                Total += row.ReadRef<DisplayName>().Value.Length;
        }
    }

    private struct DisplayName : IComponent
    {
        public string Value;
    }

    private readonly struct ChangedNameRows(List<Entity> entities) : IQueryExecution
    {
        public void Execute(QueryCursor query)
        {
            foreach (var chunk in query.Chunks)
            {
                var changes = chunk.ReadRowChanges<DisplayName>();
                foreach (int row in chunk.RowIndices)
                    if (changes.HasChanged(row))
                        entities.Add(chunk.GetEntity(row));
            }
        }
    }

    private ref struct NameLengths : IQueryExecution
    {
        private readonly Span<int> _lengths;

        public NameLengths(Span<int> lengths) => _lengths = lengths;

        public void Execute(QueryCursor query)
        {
            int index = 0;
            foreach (var row in query.Rows)
                _lengths[index++] = row.TryReadRef<DisplayName>(out var name) ? name.Value.Value.Length : -1;
        }
    }

    private ref struct TypedNameLengths(Span<int> values) : IQueryExecution
    {
        private readonly Span<int> _values = values;

        public void Execute(QueryCursor query)
        {
            int index = 0;
            var rows = query.ReadRows<Position, DisplayName>();
            while (rows.MoveNext(out var row, out var position, out var name))
                _values[index++] = (int)position.Value.X + (name.HasValue ? name.Value.Value.Length : 0);
        }
    }
}
