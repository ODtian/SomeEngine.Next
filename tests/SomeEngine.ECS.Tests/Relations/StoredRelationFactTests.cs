using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class StoredRelationFactTests
{
    [Fact]
    public void PersistentMultiEntityFactsKeepStoredPositionRolesAndEntityLifetime()
    {
        using var world = new World();
        var source = world.Create(new Score { Value = 3 });
        var carrier = world.Create(new Weight { Value = 8 });
        var destination = world.Create(new Score { Value = 6 });
        var lightDestination = world.Create(new Score { Value = 1 });
        world.Create(new TransferFact { Source = source, Carrier = carrier, Destination = destination });
        var selectedFact = world.Create(new TransferFact
        {
            Source = destination, Carrier = carrier, Destination = lightDestination,
        });
        var facts = world.Query(new QueryDescription().WithStored<TransferFact>());
        var selected = world.Transfers(facts).Where(new TransferFits());
        var rows = new List<(Entity, Entity, Entity)>();
        var output = new TransferList(rows);

        selected.Enumerate(ref output);

        Assert.Equal(new[] { (destination, carrier, lightDestination) }, rows);
        Assert.Equal(1, selected.Count());

        world.Destroy(selectedFact);

        Assert.Equal(0, selected.Count());
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
