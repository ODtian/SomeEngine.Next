using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class ScopedQueryStateTests
{
    [Fact]
    public void CallbackQueryWritesMatchingValuesIntoStackBackedState()
    {
        using var world = new World();
        world.CreateEntity(new Position { X = 2 });
        world.CreateEntity(new Position { X = 5 });
        world.CreateEntity(new Velocity { X = 99 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        Span<float> values = stackalloc float[2];
        var state = new PositionValues(values);

        world.ExecuteQuery(query, ref state, ReadPositions);

        values.Sort();
        Assert.Equal(2, state.Count);
        Assert.Equal([2f, 5f], values.ToArray());
    }

    [Fact]
    public void VersionedCallbackQueryWritesOnlyChangedValuesIntoStackBackedState()
    {
        using var world = new World();
        var changed = world.CreateEntity(new Position { X = 2 });
        world.CreateEntity(new Position { X = 5 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>().Changed<Position>());
        uint baseline = world.AcquireSystemTick();
        world.Replace(changed, new Position { X = 9 });
        Span<float> values = stackalloc float[1];
        var state = new PositionValues(values);

        world.ExecuteQuery(query, baseline, ref state, ReadPositions);

        Assert.Equal(1, state.Count);
        Assert.Equal([9f], values.ToArray());
    }

    private static void ReadPositions(scoped QueryCursor query, ref PositionValues state)
    {
        foreach (var row in query.Rows)
            state.Values[state.Count++] = row.Read<Position>().X;
    }

    private ref struct PositionValues(Span<float> values)
    {
        public Span<float> Values = values;
        public int Count;
    }
}
