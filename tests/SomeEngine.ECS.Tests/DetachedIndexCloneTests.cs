using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedIndexCloneTests
{
    [Fact]
    public void IndexClone_DetachesWorkingBucketsAndRetainsImmutablePublishedGeneration()
    {
        var world = new World();
        Entity first = world.CreateEntity(new IndexedNumber(7));
        Entity second = world.CreateEntity(new IndexedNumber(7));

        ReadOnlySpan<Entity> sourceGeneration =
            world.GetByIndex<IndexedNumber, int>(7);
        Assert.Equal(new[] { first, second }, sourceGeneration.ToArray());

        int componentId = SomeEngine.ECS.Registry.ComponentMetadata<IndexedNumber>.Id;
        object sourceBacking = world.Indices.StoreBackingIdentity(componentId) ??
            throw new InvalidOperationException("Expected a materialized index store.");
        SomeEngine.ECS.Owners.Indices candidate = world.Indices.CloneDetached();
        Assert.Same(sourceBacking, candidate.StoreBackingIdentity(componentId));
        Assert.Equal(0, candidate.StoreDetachCount(componentId));

        candidate.Drop(first, new IndexedNumber(7));
        candidate.Fix(first, new IndexedNumber(9));

        Assert.NotSame(sourceBacking, candidate.StoreBackingIdentity(componentId));
        Assert.Equal(1, candidate.StoreDetachCount(componentId));

        Assert.Equal(
            new[] { first, second },
            sourceGeneration.ToArray());
        Assert.Equal(
            new[] { first, second },
            world.GetByIndex<IndexedNumber, int>(7).ToArray());
        Assert.Equal(
            new[] { second },
            candidate.Get<IndexedNumber, int>(7, world.Tables.All).ToArray());
        Assert.Equal(
            new[] { first },
            candidate.Get<IndexedNumber, int>(9, world.Tables.All).ToArray());
    }

    private readonly record struct IndexedNumber(int Value) : IIndexedComponent<int>
    {
        public int GetKey() => Value;
    }
}
