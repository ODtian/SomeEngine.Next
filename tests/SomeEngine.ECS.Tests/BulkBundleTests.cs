using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public class BulkBundleTests
{
    [Fact]
    public void ReservedSingleComponentBatchCreatesEveryRequestedValue()
    {
        using var world = new World();
        int[] ids = [ComponentMetadata<Position>.Id];
        world.ReserveBundle(ids, 100_000);
        world.ExecuteBundleSpawnBatch(ids, 100_000, view =>
        {
            var position = new Position { X = view.Index, Y = view.Index + 1 };
            view.Write(in position);
        });
        var positions = ReadPositions(world);
        Assert.Equal(100_000, world.EntityCount);
        Assert.Equal(Enumerable.Range(0, 100_000).Select(value => (float)value), positions.Select(position => position.X));
        Assert.All(positions, position => Assert.Equal(position.X + 1, position.Y));
    }

    [Fact]
    public void ReservedMultiComponentBatchCreatesEveryRequestedValue()
    {
        using var world = new World();
        int[] ids = [ComponentMetadata<Position>.Id, ComponentMetadata<Velocity>.Id, ComponentMetadata<Health>.Id];
        world.ReserveBundle(ids, 100_000);
        world.ExecuteBundleSpawnBatch(ids, 100_000, view =>
        {
            var position = new Position { X = view.Index };
            var velocity = new Velocity { X = view.Index + 2 };
            var health = new Health { Value = view.Index + 4 };
            view.Write(in position);
            view.Write(in velocity);
            view.Write(in health);
        });
        int count = 0;
        var query = world.Query(world.QueryDefinition().Read<Position>().Read<Velocity>().Read<Health>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(row.Read<Position>().X + 2, row.Read<Velocity>().X);
                Assert.Equal(row.Read<Position>().X + 4, row.Read<Health>().Value);
                count++;
            }
        });
        Assert.Equal(100_000, count);
    }

    [Fact]
    public void BatchAndScalarCreationProduceEquivalentRows()
    {
        using var batch = new World();
        using var scalar = new World();
        int[] ids = [ComponentMetadata<Position>.Id];
        batch.ExecuteBundleSpawnBatch(ids, 32, view =>
        {
            var position = new Position { X = view.Index, Y = view.Index + 1 };
            view.Write(in position);
        });
        for (int i = 0; i < 32; i++)
            scalar.CreateEntity(new Position { X = i, Y = i + 1 });
        Assert.Equal(32, batch.EntityCount);
        Assert.Equal(32, scalar.EntityCount);
        Assert.Equal(ReadPositions(scalar), ReadPositions(batch));
    }

    private static Position[] ReadPositions(World world)
    {
        var values = new List<Position>();
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                values.Add(row.Read<Position>());
        });
        return values.OrderBy(position => position.X).ToArray();
    }
}
