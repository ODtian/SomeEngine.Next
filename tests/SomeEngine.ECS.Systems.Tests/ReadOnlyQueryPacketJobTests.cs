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

    [Fact]
    public void RowFiltersBecomeContiguousRangesWithDisjointOutputRanges()
    {
        World world = CreateJobWorld();
        Entity[] source = new Entity[6];
        for (int index = 0; index < source.Length; index++)
            source[index] = world.Create(new RangeValue(index + 10), default(RangeGate));
        world.Disable<RangeGate>(source[1]);
        world.Disable<RangeGate>(source[4]);
        Query query = world.Query(new QueryDescription().WithStored<RangeValue>().With<RangeGate>());
        var expected = new List<Entity>();
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
                foreach (Entity entity in chunk.Entities) expected.Add(entity);
        Dictionary<Entity, int> input = source.Select((entity, index) => new KeyValuePair<Entity, int>(entity, index + 10)).ToDictionary();
        int[] values = new int[4], starts = new int[4], counts = new int[4];
        Entity[] entities = new Entity[4];
        Array.Fill(starts, -1);
        using (QueryScope rows = query.OpenRead())
        {
            var ranges = new List<ReadRange>();
            int output = 0;
            foreach (QueryChunk chunk in rows)
                for (int start = 0; start < chunk.Count; start += 2)
                {
                    QueryChunk part = chunk.Slice(start, Math.Min(2, chunk.Count - start));
                    ranges.Add(new(part, output, ranges.Count));
                    output += part.Count;
                }
            ReadRange[] work = ranges.ToArray();
            world.Execution!.ExecuteRanges(work, work.Length, new CopyRows(values, entities, starts, counts));
            Assert.Equal(4, output);
        }
        Assert.Equal(expected, entities);
        Assert.Equal(expected.Select(entity => input[entity]), values);
        Assert.All(counts, count => Assert.InRange(count, 0, 2));
        Assert.Equal(Enumerable.Range(0, 4), starts.Zip(counts)
            .Where(range => range.Second > 0).SelectMany(range => Enumerable.Range(range.First, range.Second)));
    }

    [Fact]
    public void RangeReadRejectsComponentsNotDeclaredByTheQuery()
    {
        World world = CreateJobWorld();
        world.Create(new RangeValue(7), new UndeclaredValue(9));
        Query query = world.Query<RangeValue>();
        using QueryScope rows = query.OpenRead();
        QueryChunk[] chunks = new QueryChunk[1];
        foreach (QueryChunk chunk in rows) chunks[0] = chunk;
        Assert.Throws<JobResourceSafetyException>(() =>
            world.Execution!.ExecuteRanges(chunks, chunks.Length, new UndeclaredRead()));
    }

    [Fact]
    public void ReadRangesServeSeveralPassesAndCommandRecordingInsideTheirScope()
    {
        World world = CreateJobWorld();
        Entity[] entities = Enumerable.Range(0, 5).Select(index => world.Create(new RangeValue(index + 20))).ToArray();
        Query query = world.Query<RangeValue>();
        int[] first = new int[5], second = new int[5];
        using var commands = new CommandBatch(world, 3);
        using (QueryScope rows = query.OpenRead())
        {
            var ranges = new List<ReadRange>();
            int output = 0;
            foreach (QueryChunk chunk in rows)
                for (int start = 0; start < chunk.Count; start += 2)
                {
                    QueryChunk part = chunk.Slice(start, Math.Min(2, chunk.Count - start));
                    ranges.Add(new(part, output, ranges.Count));
                    output += part.Count;
                }
            ReadRange[] work = ranges.ToArray();
            world.Execution!.ExecuteRanges(work, work.Length, new CopyValues(first));
            world.Execution.ExecuteRanges(work, work.Length, new CopyValues(second));
            world.Execution.ExecuteRanges(work, work.Length, new RecordMarkers(commands));
        }
        Assert.Equal([20, 21, 22, 23, 24], first);
        Assert.Equal(first, second);
        commands.Playback();
        for (int index = 0; index < entities.Length; index++)
            Assert.Equal(index + 20, world.ReadComponentData<RangeMarker>(entities[index]).Value);
        Assert.Throws<InvalidOperationException>(() => commands[0].Add(entities[0], new RangeMarker(99)));
    }

    private readonly record struct ReadRange(QueryChunk Chunk, int OutputStart, int Index);
    private readonly record struct CopyRows(int[] Values, Entity[] Entities, int[] Starts, int[] Counts) : IRangeWork<ReadRange>
    {
        public void Execute(in ReadRange range)
        {
            Starts[range.Index] = range.OutputStart;
            Counts[range.Index] = range.Chunk.Count;
            ReadOnlySpan<RangeValue> values = range.Chunk.Read<RangeValue>();
            for (int row = 0; row < range.Chunk.Count; row++)
            {
                Values[range.OutputStart + row] = values[row].Value;
                Entities[range.OutputStart + row] = range.Chunk.Entities[row];
            }
        }
    }
    private readonly struct UndeclaredRead : IRangeWork<QueryChunk>
    {
        public void Execute(in QueryChunk chunk) => _ = chunk.Read<UndeclaredValue>();
    }
    private readonly struct CopyValues(int[] values) : IRangeWork<ReadRange>
    {
        public void Execute(in ReadRange range)
        {
            ReadOnlySpan<RangeValue> source = range.Chunk.Read<RangeValue>();
            for (int row = 0; row < source.Length; row++) values[range.OutputStart + row] = source[row].Value;
        }
    }
    private readonly struct RecordMarkers(CommandBatch commands) : IRangeWork<ReadRange>
    {
        public void Execute(in ReadRange range)
        {
            ReadOnlySpan<RangeValue> values = range.Chunk.Read<RangeValue>();
            for (int row = 0; row < values.Length; row++)
                commands[range.Index].Add(range.Chunk.Entities[row], new RangeMarker(values[row].Value));
        }
    }
    private readonly record struct RangeValue(int Value) : IComponent, ITrackedComponent;
    private readonly record struct RangeElement(int Value) : IBufferElement, ITrackedComponent;
    private readonly record struct UndeclaredValue(int Value) : IComponent;
    private readonly record struct RangeMarker(int Value) : IComponent;
    private readonly struct RangeGate : IEnableableComponent;
}
