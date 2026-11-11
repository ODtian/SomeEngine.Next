using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public class BulkBundleTests
{
    [Fact]
    public void ReservedSingleComponentBatchCreatesEveryRequestedValue()
    {
        using var world = new World();
        int[] ids = [ComponentId<Position>.Value];
        world.ReserveBundle(ids, 100_000);
        world.ExecuteBundleSpawnBatch(ids, 100_000, 0, new PositionWriter());
        var positions = ReadPositions(world);
        Assert.Equal(100_000, world.EntityCount);
        Assert.Equal(Enumerable.Range(0, 100_000).Select(value => (float)value), positions.Select(position => position.X));
        Assert.All(positions, position => Assert.Equal(position.X + 1, position.Y));
    }

    [Fact]
    public void ReservedMultiComponentBatchCreatesEveryRequestedValue()
    {
        using var world = new World();
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value, ComponentId<Health>.Value];
        world.ReserveBundle(ids, 100_000);
        world.ExecuteBundleSpawnBatch(ids, 100_000, 0, new MotionWriter());
        int count = 0;
        var query = world.Query(new QueryDescription().WithStored<Position>().WithStored<Velocity>().WithStored<Health>());
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
            {
                Assert.Equal(rowChunk.Read<Position>()[row].X + 2, rowChunk.Read<Velocity>()[row].X);
                Assert.Equal(rowChunk.Read<Position>()[row].X + 4, rowChunk.Read<Health>()[row].Value);
                count++;
            }

        };
        Assert.Equal(100_000, count);
    }

    [Fact]
    public void BatchAndScalarCreationProduceEquivalentRows()
    {
        using var batch = new World();
        using var scalar = new World();
        int[] ids = [ComponentId<Position>.Value];
        batch.ExecuteBundleSpawnBatch(ids, 32, 0, new PositionWriter());
        for (int i = 0; i < 32; i++)
            scalar.Create(new Position { X = i, Y = i + 1 });
        Assert.Equal(32, batch.EntityCount);
        Assert.Equal(32, scalar.EntityCount);
        Assert.Equal(ReadPositions(scalar), ReadPositions(batch));
    }

    private readonly struct PositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int offset)
            => view.Write(new Position { X = view.Index + offset, Y = view.Index + offset + 1 });
    }
    private readonly struct MotionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int offset)
        {
            view.Write(new Position { X = view.Index + offset });
            view.Write(new Velocity { X = view.Index + offset + 2 });
            view.Write(new Health { Value = view.Index + offset + 4 });
        }
    }

    private static Position[] ReadPositions(World world)
    {
        var values = new List<Position>();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                values.Add(rowChunk.Read<Position>()[row]);

        };
        return values.OrderBy(position => position.X).ToArray();
    }
}
