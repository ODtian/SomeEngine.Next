using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public sealed class GeneratedBundleTests
{
    [Fact]
    public void NestedBundleWritesTableTagSharedSparseAndBufferValues()
    {
        using var world = new World();
        BundleItem[] items = [new(4), new(5)];
        Entity entity = world.Spawn(new BundleValues
        {
            Pair = new() { Position = new(2), Tag = new() },
            Scene = new(7), Damage = new(3), Items = items, Name = new("first"),
        });
        items[0] = new(40);

        Assert.Equal(2, world.Read<BundlePosition>(entity).X);
        Assert.True(world.Has<BundleTag>(entity));
        Assert.Equal(7, world.ReadShared<BundleScene>(entity).Value);
        Assert.Equal(3, world.ReadSparse<BundleDamage>(entity).Amount);
        using ReadOnlyBuffer<BundleItem> buffer = world.ReadBuffer<BundleItem>(entity);
        Assert.Equal([4, 5], buffer.AsSpan().ToArray().Select(item => item.Value));
    }

    [Fact]
    public void BatchBundleCreationPublishesEachFinalMemberOnce()
    {
        using var world = new World(chunkCapacity: 64);
        using EventReader<MembershipChange> reader = world.WatchMembership(world.Query<BundlePosition>());
        var bundles = new BundleValues[130];
        var entities = new Entity[bundles.Length];
        for (int index = 0; index < bundles.Length; index++)
            bundles[index] = new() { Pair = new() { Position = new(index) }, Scene = new(7), Name = new("batch") };

        world.Spawn(bundles, entities);

        Assert.Equal(entities.Length, world.EntityCount);
        using EventRead<MembershipChange> changes = reader.Open();
        Assert.Equal(entities, changes.First.ToArray().Select(change => change.Entity));
        Assert.All(changes.First.ToArray(), change => Assert.Equal(MembershipChangeKind.Added, change.Kind));
        ulong version = world.ComponentDataWriteVersion<BundlePosition>(entities[0]);
        for (int index = 0; index < entities.Length; index++)
        {
            Assert.Equal(index, world.Read<BundlePosition>(entities[index]).X);
            Assert.Equal(version, world.ComponentDataWriteVersion<BundlePosition>(entities[index]));
        }
    }

    [Fact]
    public void BundleAddAndReplaceMaintainTheSameEntityAndItsIndexAndSharedBindings()
    {
        using var world = new World();
        ValueIndex<BundleName, string> index = world.Index<BundleName, string>();
        Entity entity = world.Create();
        world.AddBundle(entity, new BundleValues
        {
            Pair = new() { Position = new(2) }, Scene = new(7), Damage = new(3),
            Name = new("first"), Items = new BundleItem[] { new(4), new(5) },
        });
        world.ReplaceBundle(entity, new BundleValues
        {
            Pair = new() { Position = new(9) }, Scene = new(10), Damage = new(7),
            Name = new("second"), Items = new BundleItem[] { new(8) },
        });

        Assert.Equal(1, world.EntityCount);
        Assert.Equal(9, world.Read<BundlePosition>(entity).X);
        Assert.Equal(10, world.ReadShared<BundleScene>(entity).Value);
        Assert.Equal(7, world.ReadSparse<BundleDamage>(entity).Amount);
        using IndexMembers previous = index.Find("first");
        using IndexMembers current = index.Find("second");
        Assert.Equal(0, previous.Count);
        Assert.Equal([entity], current.Entities.ToArray());
        using ReadOnlyBuffer<BundleItem> buffer = world.ReadBuffer<BundleItem>(entity);
        Assert.Equal([8], buffer.AsSpan().ToArray().Select(item => item.Value));
    }
}

internal readonly record struct BundlePosition(int X) : IAddedComponent;
internal readonly struct BundleTag : ITag;
internal readonly record struct BundleScene(int Value) : ISharedComponent, IAddedComponent;
internal readonly record struct BundleDamage(int Amount) : ISparseComponent, IAddedComponent;
internal readonly record struct BundleItem(int Value) : IBufferElement, IAddedComponent;
internal readonly record struct BundleName(string Value) : IIndexedComponent<string>, IAddedComponent
{
    public string GetKey() => Value;
}
internal struct BundlePair : IComponentBundle
{
    public BundlePosition Position;
    public BundleTag Tag;
}
internal struct BundleValues : IComponentBundle
{
    public BundlePair Pair;
    public BundleScene Scene;
    public BundleDamage Damage;
    public ReadOnlyMemory<BundleItem> Items;
    public BundleName Name;
}
