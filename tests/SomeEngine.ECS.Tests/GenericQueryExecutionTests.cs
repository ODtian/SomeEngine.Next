using SomeEngine.ECS.Queries;

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
}
