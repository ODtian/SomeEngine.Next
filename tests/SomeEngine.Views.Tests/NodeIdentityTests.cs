using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class NodeIdentityTests
{
    [Fact]
    public void KeyedRowsKeepTheirEntitiesAndLocalStateWhenReordered()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RowList([11, 22, 33]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        frames.World.Set(rows[1], new Selection(7));
        frames.World.Set(root, new RowList([33, 11, 22]));

        frames.Frame();

        Assert.Equal(new[] { rows[2], rows[0], rows[1] },
            ElementChildren.Read(frames.World, root));
        Assert.Equal(7, frames.World.ReadComponentData<Selection>(rows[1]).Value);
        Assert.Equal(22UL, frames.World.ReadComponentData<Row>(rows[1]).Id);
    }

    [Fact]
    public void RemovingTheMiddleRowReleasesItsSubtreeAndKeepsOtherRows()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RowList([11, 22, 33]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, rows[1]));
        Entity tip = Assert.Single(ElementChildren.Read(frames.World, leaf));
        frames.World.Set(rows[1], new Row(99));
        frames.World.Set(root, new RowList([11, 33]));

        frames.Frame();

        Assert.Equal(new[] { rows[0], rows[2] }, ElementChildren.Read(frames.World, root));
        Assert.False(frames.World.IsAlive(rows[1]) && frames.World.Has<ViewType>(rows[1]));
        Assert.False(frames.World.IsAlive(leaf) && frames.World.Has<ViewType>(leaf));
        Assert.False(frames.World.IsAlive(tip) && frames.World.Has<ViewType>(tip));
        Assert.Equal(33UL, frames.World.ReadComponentData<Row>(rows[2]).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingTheViewTypeAtTheSameKeyReplacesItsEntityAndSubtree(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RowOrLeaf(false, sparse));
        frames.Frame();
        Entity row = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, row));
        frames.World.Set(root, new RowOrLeaf(true, sparse));

        frames.Frame();

        Entity replacement = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.NotEqual(row, replacement);
        Assert.False(frames.World.IsAlive(row) && frames.World.Has<ViewType>(row));
        Assert.False(frames.World.IsAlive(leaf) && frames.World.Has<ViewType>(leaf));
        Assert.Equal(44UL, sparse
            ? frames.World.ReadComponentData<SparseLeaf>(replacement).Id
            : frames.World.ReadComponentData<Leaf>(replacement).Id);
        Assert.Equal(8UL, frames.World.ReadComponentData<ElementKey>(replacement).Key);
        Assert.Equal(0, frames.World.ReadComponentData<ElementKey>(replacement).Occurrence);
    }

    [Fact]
    public void ZeroKeyRowsReuseTheirEntitiesByOccurrence()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ZeroKeyRows([11, 22, 33]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        frames.World.Set(rows[0], new Selection(5));
        frames.World.Set(root, new ZeroKeyRows([33, 11, 22]));

        frames.Frame();

        Assert.Equal(rows, ElementChildren.Read(frames.World, root));
        Assert.Equal(33UL, frames.World.ReadComponentData<Row>(rows[0]).Id);
        Assert.Equal(5, frames.World.ReadComponentData<Selection>(rows[0]).Value);
        Assert.Equal(new[] { 0, 1, 2 }, rows.Select(row => frames.World.ReadComponentData<ElementKey>(row).Occurrence).ToArray());
        Assert.All(rows, row => Assert.Equal(0UL, frames.World.ReadComponentData<ElementKey>(row).Key));
    }

    [Fact]
    public void RepeatedKeysWithoutLocalStateMatchByTheirOwnOccurrence()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RepeatedLeaves(false));
        frames.Frame();
        Entity[] children = ElementChildren.Read(frames.World, root);
        frames.World.Set(root, new RepeatedLeaves(true));

        frames.Frame();

        Entity[] reordered = ElementChildren.Read(frames.World, root);
        Assert.Equal(new[] { children[0], children[2], children[1] }, reordered[..3]);
        Assert.Equal(40UL, frames.World.ReadComponentData<Leaf>(reordered[3]).Id);
        Assert.Equal(new[] { 0, 0, 1, 2 },
            reordered.Select(child => frames.World.ReadComponentData<ElementKey>(child).Occurrence).ToArray());
        Assert.Equal(new[] { 5UL, 9UL, 5UL, 5UL },
            reordered.Select(child => frames.World.ReadComponentData<ElementKey>(child).Key).ToArray());
    }

    [Fact]
    public void NestedKeysKeepGroupsAndExplicitCallKeysDistinctWhenReordered()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedRows([11, 22]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        frames.World.Set(root, new NestedRows([22, 11]));

        frames.Frame();

        Assert.Equal(new[] { rows[2], rows[3], rows[0], rows[1], rows[4], rows[5] },
            ElementChildren.Read(frames.World, root));
        Assert.Equal(rows.Length,
            rows.Select(row => frames.World.ReadComponentData<ElementKey>(row).Key).Distinct().Count());
        Assert.Equal(90UL, frames.World.ReadComponentData<ElementKey>(rows[4]).Key);
        Assert.Equal(0UL, frames.World.ReadComponentData<ElementKey>(rows[5]).Key);
        Assert.All(rows, row => Assert.Equal(0UL,
            frames.World.ReadComponentData<ElementKey>(Assert.Single(ElementChildren.Read(frames.World, row))).Key));
    }

#if DEBUG
    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public void ChangingRepeatedKeyCountsWithLocalStateReportsTheKey(int previousCount, int count)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new RowList(Enumerable.Repeat(42UL, previousCount).ToArray()));
        frames.Frame();
        frames.World.Set(root, new RowList(Enumerable.Repeat(42UL, count).ToArray()));

        frames.Frame();
        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadComponentData<BuildError>(root).Exception);

        Assert.Throws<InvalidOperationException>(() => Scope.Key(42));
    }
#else
    [Fact]
    public void ShippingBuildMatchesStatefulRepeatedKeysAndReleasesRemovedChildren()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RowList([42, 42]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, rows[1]));
        frames.World.Set(root, new RowList([42]));

        frames.Frame();

        Assert.Equal(rows[0], Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.False(frames.World.IsAlive(rows[1]) && frames.World.Has<ViewType>(rows[1]));
        Assert.False(frames.World.IsAlive(leaf) && frames.World.Has<ViewType>(leaf));
    }
#endif

    private readonly record struct RepeatedLeaves(bool Reordered) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Leaf(10), 5);
            if (Reordered)
                Scope.View(new Leaf(90), 9);
            using (Scope.Key(5))
                Scope.View(new Leaf(20), 0);
            if (Reordered)
                Scope.View(new Leaf(40), 5);
            else
                Scope.View(new Leaf(90), 9);
            return default;
        }
    }

    private readonly record struct NestedRows(ulong[] Ids) : IView, IComponent
    {
        public BuildTask Build()
        {
            foreach (ulong id in Ids)
            {
                using (Scope.Key(id))
                {
                    using (Scope.Key(7))
                        Scope.View(new Row(id), 3);
                    Scope.View(new Row(id), 4);
                }
            }
            Scope.View(new Row(90), 90);
            Scope.View(new Row(0), 0);
            return default;
        }
    }

    private readonly record struct ZeroKeyRows(ulong[] Ids) : IView, IComponent
    {
        public BuildTask Build()
        {
            foreach (ulong id in Ids)
                Scope.View(new Row(id), 0);
            return default;
        }
    }

    private readonly record struct RowOrLeaf(bool LeafOnly, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (LeafOnly && Sparse)
                Scope.View(new SparseLeaf(44), 8);
            else if (LeafOnly)
                Scope.View(new Leaf(44), 8);
            else
                Scope.View(new Row(44), 8);
            return default;
        }
    }

    private readonly record struct RowList(ulong[] Ids) : IView, IComponent
    {
        public BuildTask Build()
        {
            foreach (ulong id in Ids)
            {
                using (Scope.Key(id))
                    Scope.View(new Row(id), 0);
            }
            return default;
        }
    }

    private readonly record struct Row(ulong Id) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (!Scope.World.Has<Selection>(Scope.Entity))
            {
                Scope.World.Add(Scope.Entity, new Selection(0));
                Scope.World.Set(Scope.Entity, default(LocalState));
            }
            Scope.View(new Leaf(Id), 0);
            return default;
        }
    }

    private readonly record struct Leaf(ulong Id) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Tip(Id), 0);
            return default;
        }
    }

    private readonly record struct Tip(ulong Id) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct SparseLeaf(ulong Id) : IView, ISparseComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct Selection(int Value) : IComponent;
}
