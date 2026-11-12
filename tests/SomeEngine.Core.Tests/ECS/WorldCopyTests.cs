using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Core.Tests.ECS;

public sealed class WorldCopyTests
{
    [Fact]
    public void CopyPreservesTheTargetIdentityAndReplacesItsComponentStorage()
    {
        using var world = new World();
        ValueIndex<BundleName, string> names = world.Index<BundleName, string>();
        Entity source = world.Spawn(new BundleValues
        {
            Pair = new() { Position = new(2) }, Scene = new(7), Damage = new(3),
            Name = new("source"), Items = new BundleItem[] { new(4), new(5) },
        });
        Entity target = world.Create(new BundlePosition(99), new CopyExtra(8));
        world.SetSparse(target, new BundleDamage(100));
        world.AddBuffer<BundleItem>(target);

        world.Copy(source, target);

        Assert.Equal(2, world.EntityCount);
        Assert.True(world.IsAlive(target));
        Assert.False(world.Has<CopyExtra>(target));
        Assert.Equal(2, world.Read<BundlePosition>(target).X);
        Assert.Equal(7, world.ReadShared<BundleScene>(target).Value);
        Assert.Equal(3, world.ReadSparse<BundleDamage>(target).Amount);
        using (IndexMembers matches = names.Find("source")) Assert.Equal([source, target], matches.Entities.ToArray());
        var values = world.GetBuffer<BundleItem>(target);
        try { values[0] = new(40); }
        finally { values.Dispose(); }
        using (ReadOnlyBuffer<BundleItem> original = world.ReadBuffer<BundleItem>(source)) Assert.Equal(4, original[0].Value);
        ulong previous = world.ComponentDataWriteVersion<BundlePosition>(target);

        world.Copy(source, target);

        Assert.NotEqual(previous, world.ComponentDataWriteVersion<BundlePosition>(target));
        using ReadOnlyBuffer<BundleItem> copied = world.ReadBuffer<BundleItem>(target);
        Assert.Equal([4, 5], copied.AsSpan().ToArray().Select(item => item.Value));
    }
}

internal readonly record struct CopyExtra(int Value) : IAddedComponent;
