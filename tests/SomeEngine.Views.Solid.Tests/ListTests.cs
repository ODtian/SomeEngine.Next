using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Elements;
using static SomeEngine.Views.Solid.Solid;
using SolidFrames = SomeEngine.Views.Tests.HeadlessFrameDriver;

namespace SomeEngine.Views.Solid.Tests;

public sealed partial class SolidTests
{
    [Fact]
    public void ManagedAndNativeConsumersShareTheListBehavior() => ListChecks.Run();

    [Fact]
    public void ForMovesExistingRowsAndUpdatesTheirIndicesWhileKeepingTheirInitialContent()
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([10, 20, 30]);
        var sample = new State<string>("initial");
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            For(() => items.Value, (item, index) =>
            {
                Text($"{item}:{sample.Value}");
                Text(index);
            })));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity[] labels = rows.Select(row => Hierarchy<ElementTree>.GetChildren(frames.World, row)[0]).ToArray();
        labels[0].SetText(frames.World, "edited first");

        sample.Value = "latest";
        frames.Frame();
        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal(["edited first", "20:initial", "30:initial"], labels.Select(ReadText));

        items.Value = [30, 10, 20];
        frames.Frame();
        Entity[] reordered = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal([rows[2], rows[0], rows[1]], reordered);
        Assert.Equal(["30:initial", "edited first", "20:initial"], reordered.Select(row =>
            ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, row)[0])));
        Assert.Equal(["0", "1", "2"], reordered.Select(row =>
            ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, row)[1])));

        string ReadText(Entity node) => frames.World.Read<Elements.Text>(node).Value;
    }

    [Fact]
    public void IndexUpdatesTheChangedPositionAndPreservesOtherCalculationsAndInitialRowContent()
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<string>>(["one", "two", "three"]);
        var sample = new State<string>("initial");
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Index(() => items.Value, (item, index) =>
            {
                Text($"{index}:{item()}:{sample.Value}");
                Text(() => $"{item()}:{Untrack(() => sample.Value)}");
            })));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity[] labels = rows.Select(row => Hierarchy<ElementTree>.GetChildren(frames.World, row)[1]).ToArray();
        labels[0].SetText(frames.World, "edited first");
        labels[2].SetText(frames.World, "edited third");

        sample.Value = "latest";
        items.Value = ["one", "changed", "three"];
        frames.Frame();

        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal(["edited first", "changed:latest", "edited third"], labels.Select(ReadText));
        Assert.Equal(["0:one:initial", "1:two:initial", "2:three:initial"], rows.Select(row =>
            ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, row)[0])));

        items.Value = ["three", "one", "changed"];
        frames.Frame();
        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal(["three:latest", "one:latest", "changed:latest"], labels.Select(ReadText));

        string ReadText(Entity node) => frames.World.Read<Elements.Text>(node).Value;
    }

    [Fact]
    public void ForAcceptsEachAndKeepsEntityRowsThroughDataAndMembershipChanges()
    {
        using var frames = new SolidFrames();
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Total>());
        Entity first = frames.World.CreateEntity(new Total(10));
        Entity second = frames.World.CreateEntity(new Total(20));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            For(() => Scope.Each(query), (item, index) =>
            {
                Text(() => Scope.Read<Total>(item).Value);
                Text(index);
            })));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity secondLabel = Hierarchy<ElementTree>.GetChildren(frames.World, rows[1])[0];
        secondLabel.SetText(frames.World, "edited second");

        frames.World.Replace(first, new Total(15));
        frames.Frame();
        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal("15", ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, rows[0])[0]));
        Assert.Equal("edited second", ReadText(secondLabel));

        Entity third = frames.World.CreateEntity(new Total(30));
        frames.Frame();
        Entity[] expanded = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal(rows, expanded.Take(2));
        frames.World.DestroyEntity(first);
        frames.Frame();
        Entity[] remaining = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal([expanded[2], rows[1]], remaining);
        Assert.Equal(["30", "edited second"], remaining.Select(row =>
            ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, row)[0])));
        Assert.Equal(["0", "1"], remaining.Select(row =>
            ReadText(Hierarchy<ElementTree>.GetChildren(frames.World, row)[1])));
        Assert.False(frames.World.IsAlive(rows[0]));

        string ReadText(Entity node) => frames.World.Read<Elements.Text>(node).Value;
    }

    [Fact]
    public void IndexAcceptsEachAndKeepsPositionsWhileTheirEntitiesChange()
    {
        using var frames = new SolidFrames();
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Total>());
        Entity first = frames.World.CreateEntity(new Total(10));
        Entity second = frames.World.CreateEntity(new Total(20));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Index(() => Scope.Each(query), (item, index) => Text(() => Scope.Read<Total>(item()).Value))));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity secondLabel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, rows[1]));
        secondLabel.SetText(frames.World, "edited second");

        Entity third = frames.World.CreateEntity(new Total(30));
        frames.Frame();
        Entity[] expanded = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal(rows, expanded.Take(2));
        frames.World.DestroyEntity(first);
        frames.Frame();
        Entity[] remaining = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal(rows, remaining);
        Assert.Equal("30", frames.World.Read<Elements.Text>(
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, rows[0]))).Value);
        Assert.Equal("edited second", frames.World.Read<Elements.Text>(secondLabel).Value);
        Assert.False(frames.World.IsAlive(expanded[2]));

        frames.World.Replace(third, new Total(35));
        frames.Frame();
        Assert.Equal("35", frames.World.Read<Elements.Text>(
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, rows[0]))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedRowsReleaseTheirDescendantsAndCalculations(bool byPosition)
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([1, 2]);
        var sample = new State<string>("initial");
        Entity[] outputs = [frames.World.CreateEntity(new Display("empty")), frames.World.CreateEntity(new Display("empty"))];
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            if (byPosition)
                Index(() => items.Value, (item, index) => Content(item()));
            else
                For(() => items.Value, (item, index) => Content(item));
        }));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, rows[1]));
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));

        items.Value = [1];
        sample.Value = "latest";
        frames.Frame();
        Assert.Equal(rows[0], Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, list)));
        Assert.False(frames.World.IsAlive(rows[1]));
        Assert.False(frames.World.IsAlive(container));
        Assert.False(frames.World.IsAlive(label));
        Assert.Equal("latest", frames.World.Read<Display>(outputs[0]).Value);
        Assert.Equal("initial", frames.World.Read<Display>(outputs[1]).Value);

        items.Value = [];
        sample.Value = "empty";
        frames.Frame();
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, list));
        Assert.False(frames.World.IsAlive(rows[0]));
        Assert.Equal("latest", frames.World.Read<Display>(outputs[0]).Value);

        items.Value = [1, 2];
        frames.Frame();
        Entity[] returning = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal(2, returning.Length);
        Assert.DoesNotContain(rows[0], returning);
        Assert.DoesNotContain(rows[1], returning);
        Assert.Equal("empty", frames.World.Read<Display>(outputs[0]).Value);
        Assert.Equal("empty", frames.World.Read<Display>(outputs[1]).Value);

        void Content(int item)
        {
            Entity output = outputs[item - 1];
            using (Scope.View(new Group(), 0).Content())
                Text(() => sample.Value);
            Effect(() => Scope.Write<Display>(output).Value = sample.Value);
        }
    }

    [Fact]
    public void ForMatchesEqualValuesAndRepeatedItemsThroughReorderingAndRemoval()
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<DisplayValue>>([new("alpha"), new("beta"), new("alpha")]);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            For(() => items.Value, (item, index) => Text(item.Text))));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity[] labels = rows.Select(row => Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, row))).ToArray();
        labels[0].SetText(frames.World, "first alpha");
        labels[1].SetText(frames.World, "edited beta");
        labels[2].SetText(frames.World, "second alpha");

        items.Value = [new("alpha"), new("alpha"), new("beta")];
        frames.Frame();
        Assert.Equal([rows[0], rows[2], rows[1]], Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal(["first alpha", "edited beta", "second alpha"], labels.Select(node =>
            frames.World.Read<Elements.Text>(node).Value));

        items.Value = [new("alpha"), new("beta")];
        frames.Frame();
        Assert.Equal([rows[0], rows[1]], Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.False(frames.World.IsAlive(rows[2]));
        Assert.False(frames.World.IsAlive(labels[2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsRestoreTheirRowsAfterAnItemReadFailsAndRecoverOnTheNextChange(bool byPosition)
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([1, 2]);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            if (byPosition)
                Index(() => items.Value, (item, index) => Text(item));
            else
                For(() => items.Value, (item, index) => Text(() => $"{item}:{index()}"));
        }));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        string[] initial = ReadValues();

        items.Value = new InterruptedItems([2, 3, 1]);
        frames.Frame();
        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal(initial, ReadValues());

        items.Value = [2, 3, 1];
        frames.Frame();
        Entity[] recovered = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        if (byPosition)
        {
            Assert.Equal(rows, recovered.Take(2));
            Assert.Equal(["2", "3", "1"], ReadValues());
        }
        else
        {
            Assert.Equal(rows[1], recovered[0]);
            Assert.Equal(rows[0], recovered[2]);
            Assert.Equal(["2:0", "3:1", "1:2"], ReadValues());
        }

        string[] ReadValues() => Hierarchy<ElementTree>.GetChildren(frames.World, list).Select(row =>
            frames.World.Read<Elements.Text>(Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, row))).Value).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostRefreshKeepsUnchangedRowsAndTheirGetterCalculations(bool byPosition)
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([10, 20]);
        var caption = new State<string>("first");
        Entity root = Views.Spawn(frames.World, Entity.Null, new RepeatedView(() =>
        {
            string currentCaption = caption.Value;
            if (byPosition)
                Index(() => items.Value, (item, index) => Text(() => $"{currentCaption}:{item()}"));
            else
                For(() => items.Value, (item, index) => Text(() => $"{currentCaption}:{index()}"));
        }));
        frames.Frame();
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, rows[0]));
        label.SetText(frames.World, "edited first");

        caption.Value = "latest";
        frames.Frame();

        Assert.Equal(rows, Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray());
        Assert.Equal("edited first", frames.World.Read<Elements.Text>(label).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowGettersProvideCurrentValuesOutsideABuildAfterTheListChanges(bool byPosition)
    {
        using var frames = new SolidFrames();
        var items = new State<IReadOnlyList<int>>([10, 20]);
        Func<int> value = null!;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            if (byPosition)
                Index(() => items.Value, (item, index) =>
                {
                    if (index == 0)
                        value = item;
                    Text(item);
                });
            else
                For(() => items.Value, (item, index) =>
                {
                    if (item == 10)
                        value = index;
                    Text(index);
                });
        }));
        frames.Frame();
        Assert.Equal(byPosition ? 10 : 0, value());

        items.Value = [20, 10];
        frames.Frame();
        Assert.Equal(byPosition ? 20 : 1, value());

#if DEBUG
        items.Value = [];
        frames.Frame();
        Assert.Throws<InvalidOperationException>(() => value());
#endif
    }

#if DEBUG
    [Fact]
    public void ListsReportTheirRequiredScopeAndCallbacksThroughDevelopmentChecks()
    {
        Assert.Throws<InvalidOperationException>(() => For<int>(() => [], (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => Index<int>(() => [], (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => For<int>(null!, (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => For<int>(() => [], null!));
        Assert.Throws<InvalidOperationException>(() => Index<int>(null!, (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => Index<int>(() => [], null!));
        Assert.Throws<InvalidOperationException>(() => For((Func<QueryRows>)null!, (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => Index((Func<QueryRows>)null!, (_, _) => { }));
    }
#endif

    private sealed class InterruptedItems(int[] items) : IReadOnlyList<int>
    {
        public int Count => items.Length;
        public int this[int index] => index == 2 ? throw new InvalidOperationException("Item read failed.") : items[index];
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
