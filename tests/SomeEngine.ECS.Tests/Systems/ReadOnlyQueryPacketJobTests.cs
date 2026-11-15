using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ReadOnlyQueryPacketJobTests : JobTest
{
    [Fact]
    public void SlicedRangesOnlyReportComponentAndBufferChangesInsideTheirRows()
    {
        using var world = new World();
        Entity[] entities = Enumerable.Range(0, 6).Select(index => world.Create(new RangeValue(index))).ToArray();
        foreach (Entity entity in entities) world.AddBuffer<RangeElement>(entity);
        ulong baseline = world.Version;
        world.Set(entities[1], new RangeValue(42));
        using (DynamicBuffer<RangeElement> buffer = world.GetBuffer<RangeElement>(entities[4])) buffer.Add(new(7));
        Query query = world.Query(new QueryDescription().WithStored<RangeValue>().WithStored<Buffer<RangeElement>>());
        int[] changed = new int[6];
        int output = 0;
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
                for (int row = 0; row < chunk.Count; row++)
                {
                    QueryChunk slice = chunk.Slice(row, 1);
                    changed[output++] = (slice.WasWritten<RangeValue>(0, baseline) ? 1 : 0) |
                        (slice.WasWritten<Buffer<RangeElement>>(0, baseline) ? 2 : 0);
                }
        Assert.Equal([0, 1, 0, 0, 2, 0], changed);
    }

    [Fact]
    public void ChangedRowSliceChecksDoesNotThrow()
    {
        using var world = new World();
        Entity[] entities = Enumerable.Range(0, 4).Select(index => world.Create(new RangeValue(index))).ToArray();
        ulong baseline = world.Version;
        world.Set(entities[0], new RangeValue(10));
        world.Set(entities[1], new RangeValue(11));
        Query query = world.Query<RangeValue>();
        using QueryScope rows = query.OpenRead();
        foreach (QueryChunk chunk in rows)
        {
            Assert.Equal(4, chunk.Count);
            QueryChunk first = chunk.Slice(0, 2), middle = chunk.Slice(1, 2), empty = chunk.Slice(0, 0);
            Assert.True(first.WasWritten<RangeValue>(0, baseline));
            Assert.True(first.WasWritten<RangeValue>(1, baseline));
            Assert.True(middle.WasWritten<RangeValue>(0, baseline));
            Assert.False(middle.WasWritten<RangeValue>(1, baseline));
            Assert.Equal(0, empty.Count);
        }
    }

    private readonly record struct ReadRange(QueryChunk Chunk, int OutputStart, int Index);
    private readonly record struct RangeValue(int Value) : IComponent, ITrackedComponent;
    private readonly record struct RangeElement(int Value) : IBufferElement, ITrackedComponent;
    private readonly record struct UndeclaredValue(int Value) : IComponent;
    private readonly record struct RangeMarker(int Value) : IComponent;
    private readonly struct RangeGate : IEnableableComponent;
}
