using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class StoredRelationFactTests
{
    [Fact]
    public void PersistentMultiEntityFactsKeepStoredPositionRolesAndEntityLifetime()
    {
        using var world = new World();
        var source = world.CreateEntity(new Score { Value = 3 });
        var carrier = world.CreateEntity(new Weight { Value = 8 });
        var destination = world.CreateEntity(new Score { Value = 6 });
        var lightDestination = world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new TransferFact { Source = source, Carrier = carrier, Destination = destination });
        var selectedFact = world.CreateEntity(new TransferFact
        {
            Source = destination, Carrier = carrier, Destination = lightDestination,
        });
        var facts = world.Query(world.QueryDefinition().All<TransferFact>());
        var selected = world.Transfers(facts).Where(new TransferFits());
        var rows = new List<(Entity, Entity, Entity)>();
        var output = new TransferList(rows);

        selected.Enumerate(ref output);

        Assert.Equal(new[] { (destination, carrier, lightDestination) }, rows);
        Assert.Equal(1, selected.Count());

        world.DestroyEntity(selectedFact);

        Assert.Equal(0, selected.Count());
        world.ReleaseQuery(facts);
    }

    private struct TransferList(List<(Entity, Entity, Entity)> rows) : IRelationOutput<(Entity, Entity, Entity)>
    {
        public bool Add(in (Entity, Entity, Entity) row)
        {
            rows.Add(row);
            return true;
        }
    }
}
