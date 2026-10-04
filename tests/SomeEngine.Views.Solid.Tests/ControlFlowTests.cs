using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
using static SomeEngine.Views.Solid.Solid;
using SolidFrames = SomeEngine.Views.Tests.HeadlessFrameDriver;

namespace SomeEngine.Views.Solid.Tests;

public sealed partial class SolidTests
{
    [Fact]
    public void ShowValueGetterReadsTheLatestConditionOnEachCall() => ControlFlowChecks.LatestValueGetter();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowGettersFollowTheCurrentConditionSourceWhileRetainingTheirBranch(bool jobWorld) =>
        ControlFlowChecks.ChangedValueSource(jobWorld);

    [Fact]
    public void ManagedAndNativeConsumersShareTheControlFlowBehavior() => ControlFlowChecks.Run();

    [Fact]
    public void ShowPreservesItsBranchWhenContentOrATrueConditionValueChanges()
    {
        using var frames = new SolidFrames();
        var count = new State<int>(1);
        var sample = new State<string>("initial");
        Entity label = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Show(() => count.Value > 0, () => label = Text(sample.Value))));
        frames.Frame();
        Entity first = label;
        Assert.Equal("initial", frames.World.Read<Elements.Text>(first).Value);

        sample.Value = "latest";
        frames.Frame();
        Assert.Equal(first, label);
        Assert.True(frames.World.IsAlive(first));
        Assert.Equal("initial", frames.World.Read<Elements.Text>(first).Value);

        count.Value = 2;
        frames.Frame();
        Assert.Equal(first, label);
        Assert.True(frames.World.IsAlive(first));
        Assert.Equal("initial", frames.World.Read<Elements.Text>(first).Value);
    }

    [Fact]
    public void ShowReleasesItsBranchAndDescendantsAndCreatesTheFallbackAndReturningContent()
    {
        using var frames = new SolidFrames();
        var visible = new State<bool>(true);
        Entity branch = Entity.Null;
        Entity label = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => visible.Value, () =>
        {
            branch = Scope.Entity;
            using (Scope.View(new Group(), 0).Content())
                label = Text("visible");
        }, () => fallback = Text("fallback"))));
        frames.Frame();
        Entity firstBranch = branch;
        Entity firstLabel = label;
        Entity container = Hierarchy<ElementTree>.GetParent(frames.World, firstLabel);

        visible.Value = false;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstBranch));
        Assert.False(frames.World.IsAlive(container));
        Assert.False(frames.World.IsAlive(firstLabel));
        Assert.Equal("fallback", frames.World.Read<Elements.Text>(fallback).Value);
        Entity firstFallback = fallback;

        visible.Value = true;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstFallback));
        Assert.NotEqual(firstBranch, branch);
        Assert.NotEqual(firstLabel, label);
        Assert.Equal("visible", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void ShowValueGettersUpdateBindingsAndPreserveTheBranchUntilItsValueBecomesNull()
    {
        using var frames = new SolidFrames();
        var name = new State<string?>("first");
        Entity sampled = Entity.Null;
        Entity bound = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => name.Value, value =>
        {
            sampled = Text(value());
            bound = Text(value);
        }, () => fallback = Text("empty"))));
        frames.Frame();
        Entity firstSample = sampled;
        Entity firstBinding = bound;
        Assert.Equal("first", frames.World.Read<Elements.Text>(sampled).Value);

        name.Value = "second";
        frames.Frame();
        Assert.Equal(firstSample, sampled);
        Assert.Equal(firstBinding, bound);
        Assert.Equal("first", frames.World.Read<Elements.Text>(sampled).Value);
        Assert.Equal("second", frames.World.Read<Elements.Text>(bound).Value);

        name.Value = null;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstSample));
        Assert.False(frames.World.IsAlive(firstBinding));
        Assert.Equal("empty", frames.World.Read<Elements.Text>(fallback).Value);
        Entity empty = fallback;

        name.Value = "third";
        frames.Frame();
        Assert.False(frames.World.IsAlive(empty));
        Assert.NotEqual(firstSample, sampled);
        Assert.NotEqual(firstBinding, bound);
        Assert.Equal("third", frames.World.Read<Elements.Text>(sampled).Value);
        Assert.Equal("third", frames.World.Read<Elements.Text>(bound).Value);
    }

    [Fact]
    public void KeyedShowRecreatesChangedValuesAndPreservesEqualValuesEvenWithTheSameHash()
    {
        using var frames = new SolidFrames();
        var number = new State<int>(1);
        Entity label = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(
            () => new DisplayValue(number.Value % 2 == 0 ? "even" : "odd"),
            value => label = Text(value().Text), keyed: true)));
        frames.Frame();
        Entity odd = label;
        Assert.Equal("odd", frames.World.Read<Elements.Text>(label).Value);
        label.SetText(frames.World, "edited odd");

        number.Value = 3;
        frames.Frame();
        Assert.Equal(odd, label);
        Assert.Equal("edited odd", frames.World.Read<Elements.Text>(label).Value);

        number.Value = 2;
        frames.Frame();
        Assert.False(frames.World.IsAlive(odd));
        Assert.NotEqual(odd, label);
        Assert.Equal("even", frames.World.Read<Elements.Text>(label).Value);
        Entity even = label;
        label.SetText(frames.World, "edited even");

        number.Value = 4;
        frames.Frame();
        Assert.Equal(even, label);
        Assert.Equal("edited even", frames.World.Read<Elements.Text>(label).Value);

        number.Value = 1;
        frames.Frame();
        Assert.False(frames.World.IsAlive(even));
        Assert.NotEqual(odd, label);
        Assert.Equal("odd", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void ShowAppliesUpdatedKeyedOptionsToTheCurrentConditionValue()
    {
        using var frames = new SolidFrames();
        var name = new State<string?>("first");
        var keyed = new State<bool>();
        Entity label = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new RepeatedView(() => Show(() => name.Value,
            value => label = Text(value()), keyed: keyed.Value)));
        frames.Frame();
        Entity first = label;

        keyed.Value = true;
        frames.Frame();
        Assert.Equal(first, label);
        Assert.Equal("first", frames.World.Read<Elements.Text>(label).Value);

        name.Value = "second";
        frames.Frame();
        Assert.False(frames.World.IsAlive(first));
        Assert.NotEqual(first, label);
        Assert.Equal("second", frames.World.Read<Elements.Text>(label).Value);
        Entity second = label;

        keyed.Value = false;
        name.Value = "third";
        frames.Frame();
        Assert.Equal(second, label);
        Assert.Equal("second", frames.World.Read<Elements.Text>(label).Value);

        keyed.Value = true;
        name.Value = "fourth";
        frames.Frame();
        Assert.False(frames.World.IsAlive(second));
        Assert.NotEqual(second, label);
        Assert.Equal("fourth", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void SwitchSelectsTheFirstMatchAndRecreatesContentWhenTheSelectedMatchChanges()
    {
        using var frames = new SolidFrames();
        var first = new State<int>(1);
        var second = new State<bool>(true);
        var sample = new State<string>("initial");
        Entity label = Entity.Null;
        Action content = () => label = Text(sample.Value);
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Switch(
            Match(() => first.Value > 0, content),
            Match(() => second.Value, content))));
        frames.Frame();
        Entity firstLabel = label;
        label.SetText(frames.World, "edited first");

        sample.Value = "latest";
        first.Value = 2;
        second.Value = false;
        frames.Frame();
        Assert.Equal(firstLabel, label);
        Assert.Equal("edited first", frames.World.Read<Elements.Text>(label).Value);

        first.Value = 0;
        second.Value = true;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstLabel));
        Assert.NotEqual(firstLabel, label);
        Assert.Equal("latest", frames.World.Read<Elements.Text>(label).Value);
        Entity secondLabel = label;
        label.SetText(frames.World, "edited second");

        first.Value = -1;
        frames.Frame();
        Assert.Equal(secondLabel, label);
        Assert.Equal("edited second", frames.World.Read<Elements.Text>(label).Value);

        second.Value = false;
        frames.Frame();
        Assert.False(frames.World.IsAlive(secondLabel));

        first.Value = 1;
        frames.Frame();
        Assert.NotEqual(firstLabel, label);
        Assert.NotEqual(secondLabel, label);
        Assert.Equal("latest", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void DynamicPreservesTheCurrentComponentAndRecreatesItWhenItsFunctionChanges()
    {
        using var frames = new SolidFrames();
        var selection = new State<int>();
        var sample = new State<string>("initial");
        Entity label = Entity.Null;
        Action first = () => label = Text("first " + sample.Value);
        Action second = () => label = Text("second " + sample.Value);
        Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Dynamic(() => selection.Value % 2 == 0 ? first : second)));
        frames.Frame();
        Entity firstLabel = label;
        label.SetText(frames.World, "edited first");

        sample.Value = "latest";
        selection.Value = 2;
        frames.Frame();
        Assert.Equal(firstLabel, label);
        Assert.Equal("edited first", frames.World.Read<Elements.Text>(label).Value);

        selection.Value = 1;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstLabel));
        Assert.NotEqual(firstLabel, label);
        Assert.Equal("second latest", frames.World.Read<Elements.Text>(label).Value);
        Entity secondLabel = label;
        label.SetText(frames.World, "edited second");

        selection.Value = 3;
        frames.Frame();
        Assert.Equal(secondLabel, label);
        Assert.Equal("edited second", frames.World.Read<Elements.Text>(label).Value);

        selection.Value = 0;
        frames.Frame();
        Assert.False(frames.World.IsAlive(secondLabel));
        Assert.NotEqual(firstLabel, label);
        Assert.Equal("first latest", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void ReleasedShowContentStopsItsEffectsAndPreservesTheSurroundingContent()
    {
        using var frames = new SolidFrames();
        var visible = new State<bool>(true);
        var sample = new State<string>("first");
        Entity output = frames.World.CreateEntity(new Display("empty"));
        Entity bound = Entity.Null;
        Entity before = Entity.Null;
        Entity after = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            using (Scope.View(new Group(), 0).Content())
            {
                before = Text("before");
                Show(() => visible.Value, () =>
                {
                    bound = Text(() => sample.Value);
                    Effect(() => Scope.Write<Display>(output).Value = sample.Value);
                });
                after = Text("after");
            }
        }));
        frames.Frame();
        Entity firstBinding = bound;
        Entity firstBefore = before;
        Entity firstAfter = after;
        before.SetText(frames.World, "edited before");
        after.SetText(frames.World, "edited after");

        sample.Value = "second";
        frames.Frame();
        Assert.Equal(firstBinding, bound);
        Assert.Equal("second", frames.World.Read<Elements.Text>(bound).Value);
        Assert.Equal("second", frames.World.Read<Display>(output).Value);

        visible.Value = false;
        sample.Value = "removed";
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstBinding));
        Assert.Equal("second", frames.World.Read<Display>(output).Value);

        sample.Value = "third";
        frames.Frame();
        Assert.Equal("second", frames.World.Read<Display>(output).Value);
        Assert.Equal(firstBefore, before);
        Assert.Equal(firstAfter, after);
        Assert.Equal("edited before", frames.World.Read<Elements.Text>(before).Value);
        Assert.Equal("edited after", frames.World.Read<Elements.Text>(after).Value);

        visible.Value = true;
        frames.Frame();
        Assert.NotEqual(firstBinding, bound);
        Assert.Equal("third", frames.World.Read<Elements.Text>(bound).Value);
        Assert.Equal("third", frames.World.Read<Display>(output).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowAcceptsNullableNumbersAndTreatsZeroAsAPresentValue(bool keyed)
    {
        using var frames = new SolidFrames();
        var number = new State<int?>();
        Entity label = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => number.Value,
            value => label = Text(() => value().GetValueOrDefault()),
            () => fallback = Text("empty"), keyed)));
        frames.Frame();
        Entity empty = fallback;
        Assert.Equal("empty", frames.World.Read<Elements.Text>(fallback).Value);

        number.Value = 0;
        frames.Frame();
        Assert.False(frames.World.IsAlive(empty));
        Assert.Equal("0", frames.World.Read<Elements.Text>(label).Value);
        Entity zero = label;

        number.Value = 1;
        frames.Frame();
        Assert.Equal(keyed, zero != label);
        Assert.Equal(!keyed, frames.World.IsAlive(zero));
        Assert.Equal("1", frames.World.Read<Elements.Text>(label).Value);

        Entity one = label;
        number.Value = null;
        frames.Frame();
        Assert.False(frames.World.IsAlive(one));
        Assert.NotEqual(empty, fallback);
        Assert.Equal("empty", frames.World.Read<Elements.Text>(fallback).Value);
    }

    [Fact]
    public void SwitchRetainsTheCasesDeclaredThroughASpan()
    {
        using var frames = new SolidFrames();
        var first = new State<bool>(true);
        Entity label = Entity.Null;
        SomeEngine.Views.Solid.Match[] cases =
        [
            Match(() => first.Value, () => label = Text("first")),
            Match(() => true, () => label = Text("second")),
        ];
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Switch(cases.AsSpan())));
        frames.Frame();
        Entity firstLabel = label;
        Assert.Equal("first", frames.World.Read<Elements.Text>(label).Value);

        cases[0] = Match(() => true, () => label = Text("changed input"));
        cases[1] = Match(() => true, () => label = Text("changed input"));
        first.Value = false;
        frames.Frame();
        Assert.False(frames.World.IsAlive(firstLabel));
        Assert.Equal("second", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void SwitchCompletesItsSelectionAtTheFirstTrueCondition()
    {
        using var frames = new SolidFrames();
        Entity label = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Switch(
            Match(() => true, () => label = Text("selected")),
            Match(() => throw new InvalidOperationException("later condition"), () => Text("later")))));
        frames.Frame();
        Assert.Equal("selected", frames.World.Read<Elements.Text>(label).Value);
    }

    [Fact]
    public void ShowWithAnAbsentFallbackAndAnEmptySwitchKeepTheDeclaredSiblingContent()
    {
        using var frames = new SolidFrames();
        var visible = new State<bool>();
        Entity label = Entity.Null;
        Entity sibling = Entity.Null;
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            Show(() => visible.Value, () => label = Text("visible"));
            Switch();
            sibling = Text("sibling");
        }));
        frames.Frame();
        Assert.Equal(["sibling"], ReadTextValues(root));
        Entity firstSibling = sibling;
        sibling.SetText(frames.World, "edited sibling");

        visible.Value = true;
        frames.Frame();
        Assert.Equal(["visible", "edited sibling"], ReadTextValues(root));
        Entity visibleLabel = label;

        visible.Value = false;
        frames.Frame();
        Assert.False(frames.World.IsAlive(visibleLabel));
        Assert.Equal(["edited sibling"], ReadTextValues(root));
        Assert.Equal(firstSibling, sibling);

        string[] ReadTextValues(Entity node)
        {
            var values = new List<string>();
            Read(node);
            return values.ToArray();

            void Read(Entity current)
            {
                if (frames.World.Has<Elements.Text>(current))
                    values.Add(frames.World.Read<Elements.Text>(current).Value);
                foreach (Entity child in Hierarchy<ElementTree>.GetChildren(frames.World, current))
                    Read(child);
            }
        }
    }

#if DEBUG
    [Fact]
    public void ControlFlowCallsReportTheirRequiredBuildAndInputs()
    {
        Assert.Throws<InvalidOperationException>(() => Show(() => true, () => { }));
        Assert.Throws<InvalidOperationException>(() => Show(() => "value", _ => { }));
        Assert.Throws<InvalidOperationException>(() => Switch());
        Assert.Throws<InvalidOperationException>(() => Dynamic(() => () => { }));
        Assert.Throws<InvalidOperationException>(() => Show(null!, () => { }));
        Assert.Throws<InvalidOperationException>(() => Show(() => true, (Action)null!));
        Assert.Throws<InvalidOperationException>(() => Show<string>(null!, _ => { }));
        Assert.Throws<InvalidOperationException>(() => Show<string>(() => "value", null!));
        Assert.Throws<InvalidOperationException>(() => Match(null!, () => { }));
        Assert.Throws<InvalidOperationException>(() => Match(() => true, null!));
        Assert.Throws<InvalidOperationException>(() => Switch(default(SomeEngine.Views.Solid.Match)));
        Assert.Throws<InvalidOperationException>(() => Dynamic(null!));
    }

    [Fact]
    public void DynamicReportsAGetterThatReturnsAnAbsentComponent()
    {
        using var frames = new SolidFrames();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() => Dynamic(() => null!)));
        frames.Frame();
        Entity component = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Exception error = frames.World.ReadSparse<BuildError>(component).Exception;
        Assert.IsType<InvalidOperationException>(error);
    }
#endif

    private sealed record DisplayValue(string Text)
    {
        public override int GetHashCode() => 1;
    }

    private readonly record struct RepeatedView(Action Content) : IView, SomeEngine.ECS.IComponent
    {
        public BuildTask Build()
        {
            Content();
            return default;
        }
    }
}
