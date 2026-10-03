using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ComputationTests
{
    [Fact]
    public void ManagedAndNativeConsumersShareTheCalculationBehavior() => ComputationChecks.Verify();

    [Fact]
    public void AStateBindingUpdatesItsNodeAndKeepsTheBuiltChild()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberBinding(number));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(child, new Text("edited"));

        number.Value = 7;
        frames.Frame();

        Assert.Equal(7, frames.World.Read<Number>(root).Value);
        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal("edited", frames.World.Read<Text>(child).Value);
    }

    [Fact]
    public void AChangedParentCalculationReleasesItsPreviousBinding()
    {
        using var frames = new HeadlessFrameDriver();
        var enabled = new State<bool>(true);
        var number = new State<int>(3);
        Entity target = frames.World.CreateEntity(new Number());
        Views.Spawn(frames.World, Entity.Null, new ConditionalBinding(enabled, number, target));
        frames.Frame();
        Assert.Equal(3, frames.World.Read<Number>(target).Value);

        enabled.Value = false;
        number.Value = 7;
        frames.Frame();
        number.Value = 11;
        frames.Frame();

        Assert.Equal(-1, frames.World.Read<Number>(target).Value);
    }

    [Fact]
    public void AnEqualMemoResultPreservesTheEditedDisplayUntilItsResultChanges()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new SignBinding(number));
        frames.Frame();
        Assert.Equal("positive", frames.World.Read<Text>(root).Value);
        frames.World.Replace(root, new Text("edited"));

        number.Value = 7;
        frames.Frame();
        Assert.Equal("edited", frames.World.Read<Text>(root).Value);

        number.Value = -1;
        frames.Frame();
        Assert.Equal("negative", frames.World.Read<Text>(root).Value);
    }

    [Fact]
    public void AMemoReadUsesAStateWrittenInTheSameFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new StoredMemo(number));
        frames.Frame();
        Memo<int> doubled = frames.World.Read<MemoValue>(root).Value;
        Assert.Equal(6, doubled.Value);
        uint version = doubled.Version;

        number.Value = 7;

        Assert.Equal(14, doubled.Value);
        Assert.NotEqual(version, doubled.Version);
        version = doubled.Version;
        number.Value = 7;
        Assert.Equal(14, doubled.Value);
        Assert.Equal(version, doubled.Version);
    }

    [Fact]
    public void ReleasingAViewKeepsItsFormerBindingTargetAvailable()
    {
        using var frames = new HeadlessFrameDriver();
        var enabled = new State<bool>(true);
        var number = new State<int>(3);
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConditionalBinding(enabled, number, target));
        frames.Frame();

        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        frames.World.Replace(target, new Number(41));
        number.Value = 7;
        enabled.Value = false;
        frames.Frame();

        Assert.Equal(41, frames.World.Read<Number>(target).Value);
    }

    [Fact]
    public void MemoRecalculationKeepsLaterBindingsInTheirCreationOrder()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            var positive = Scope.Memo(() =>
            {
                Scope.Compute(() => { _ = number.Value; });
                return number.Value > 0;
            });
            Scope.World.Add(Scope.Entity, new Text("initial"));
            Scope.Compute(() => Scope.World.Replace(target, new Number(number.Value)));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Text(positive.Value ? "positive" : "negative")));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Text($"{Scope.World.Read<Text>(Scope.Entity).Value}:{number.Value}")));
        }));
        frames.Frame();
        Assert.Equal("positive:3", frames.World.Read<Text>(root).Value);

        number.Value = -1;
        frames.Frame();

        Assert.Equal("negative:-1", frames.World.Read<Text>(root).Value);
        Assert.Equal(-1, frames.World.Read<Number>(target).Value);
    }

    [Fact]
    public void AFailedBindingReportsItsErrorAndAllowsTheOtherDisplayToUpdate()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        var error = new InvalidOperationException("binding failed");
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            Scope.World.Add(Scope.Entity, new Number());
            Scope.Compute(() =>
            {
                int value = number.Value;
                if (value == 7)
                    throw error;
                Scope.World.Replace(Scope.Entity, new Number(value));
            });
        }));
        Entity sibling = Views.Spawn(frames.World, Entity.Null, new NumberBinding(number));
        frames.Frame();

        number.Value = 7;
        frames.Frame();

        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(2ul, frames.World.ReadSparse<BuildError>(root).Frame);
        Assert.Equal(3, frames.World.Read<Number>(root).Value);
        Assert.Equal(7, frames.World.Read<Number>(sibling).Value);

        number.Value = 11;
        frames.Frame();
        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Equal(11, frames.World.Read<Number>(root).Value);
    }

    [Fact]
    public void AFailedMemoRecoversItsDisplayWithAnEqualResult()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        var error = new InvalidOperationException("memo failed");
        Memo<bool> positive = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            positive = Scope.Memo(() =>
            {
                int value = number.Value;
                if (value == 7)
                    throw error;
                return value > 0;
            });
            Scope.World.Add(Scope.Entity, new Text("initial"));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Text(positive.Value ? "positive" : "negative")));
        }));
        frames.Frame();
        uint version = positive.Version;

        number.Value = 7;
        frames.Frame();
        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => positive.Value));

        number.Value = 11;
        frames.Frame();

        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Equal("positive", frames.World.Read<Text>(root).Value);
        Assert.Equal(version, positive.Version);
    }

    [Fact]
    public void ACalculationThatUpdatesItsReadPresentsTheCompletedValueInTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            Scope.World.Add(Scope.Entity, new Number());
            Scope.Compute(() =>
            {
                int value = number.Value;
                Scope.World.Replace(Scope.Entity, new Number(value));
                if (value < 3)
                    number.Value = value + 1;
            });
        }));

        frames.Frame();

        Assert.Equal(3, frames.World.Read<Number>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentBindingsAndMemosUseTheCurrentSourceData(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseNumber(3));
        else
            frames.World.Add(source, new Number(3));
        Memo<int> tripled = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            Scope.World.Add(Scope.Entity, new Number());
            tripled = Scope.Memo(() =>
                (sparse ? Scope.Read<SparseNumber>(source).Value : Scope.Read<Number>(source).Value) * 3);
            Scope.Compute(() => Scope.World.Replace(Scope.Entity, new Number(
                sparse ? Scope.Read<SparseNumber>(source).Value : Scope.Read<Number>(source).Value)));
        }));
        frames.Frame();

        if (sparse)
            frames.World.ReplaceSparse(source, new SparseNumber(7));
        else
            frames.World.Replace(source, new Number(7));

        Assert.Equal(21, tripled.Value);
        frames.Frame();
        Assert.Equal(7, frames.World.Read<Number>(root).Value);
    }

    [Fact]
    public void ChainedMemosPreserveAnEqualDisplayAndRefreshTogetherOnRead()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Memo<bool> odd = null!;
        Memo<string> label = null!;
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            odd = Scope.Memo(() => number.Value % 2 == 1);
            label = Scope.Memo(() => odd.Value ? "odd" : "even");
            Scope.World.Add(Scope.Entity, new Text("initial"));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity, new Text(label.Value)));
        }));
        frames.Frame();
        uint oddVersion = odd.Version;
        uint labelVersion = label.Version;
        frames.World.Replace(root, new Text("edited"));

        number.Value = 5;
        frames.Frame();
        Assert.Equal("edited", frames.World.Read<Text>(root).Value);
        Assert.Equal(oddVersion, odd.Version);
        Assert.Equal(labelVersion, label.Version);

        number.Value = 4;
        Assert.Equal("even", label.Value);
        frames.Frame();
        Assert.Equal("even", frames.World.Read<Text>(root).Value);
    }

    [Fact]
    public void AOnceViewBindingReadsTheUpdatedViewParameters()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ParameterBinding(3));
        frames.Frame();

        frames.World.Replace(root, new ParameterBinding(7));
        frames.Frame();

        Assert.Equal(7, frames.World.Read<Number>(root).Value);
    }

    [Fact]
    public void ARebuiltViewReleasesItsPreviousBinding()
    {
        using var frames = new HeadlessFrameDriver();
        var enabled = new State<bool>(true);
        var number = new State<int>(3);
        Entity target = frames.World.CreateEntity(new Number());
        Views.Spawn(frames.World, Entity.Null, new RebuildingView(() =>
        {
            if (enabled.Value)
                Scope.Compute(() => Scope.World.Replace(target, new Number(number.Value)));
            else
                Scope.World.Replace(target, new Number(-1));
        }));
        frames.Frame();

        enabled.Value = false;
        number.Value = 7;
        frames.Frame();
        number.Value = 11;
        frames.Frame();

        Assert.Equal(-1, frames.World.Read<Number>(target).Value);
    }

    [Fact]
    public void UntrackedMemoReadsRefreshTheDisplayWhenTheTrackedStateChanges()
    {
        using var frames = new HeadlessFrameDriver();
        var sample = new State<int>(3);
        var trigger = new State<int>(5);
        Entity root = Views.Spawn(frames.World, Entity.Null, new CalculationView(() =>
        {
            Memo<int> sampled = Scope.Memo(() => sample.Value);
            Scope.World.Add(Scope.Entity, new Number());
            Scope.Compute(() =>
            {
                int value;
                using (Scope.Untrack())
                    value = sampled.Value;
                Scope.World.Replace(Scope.Entity, new Number(value + trigger.Value));
            });
        }));
        frames.Frame();

        sample.Value = 7;
        frames.Frame();
        Assert.Equal(8, frames.World.Read<Number>(root).Value);

        trigger.Value = 11;
        frames.Frame();
        Assert.Equal(18, frames.World.Read<Number>(root).Value);
    }

#if DEBUG
    [Fact]
    public void CalculationFactoriesReportTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.Compute(() => { }));
        Assert.Throws<InvalidOperationException>(() => Scope.Memo(() => 3));
    }

    [Fact]
    public void AMemoReadReportsItsReleasedView()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new StoredMemo(number));
        frames.Frame();
        Memo<int> memo = frames.World.Read<MemoValue>(root).Value;

        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);

        var error = Assert.Throws<InvalidOperationException>(() => memo.Value);
        Assert.Contains("active", error.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    private readonly record struct RebuildingView(Action Content) : IView, IComponent
    {
        public BuildTask Build()
        {
            Content();
            return default;
        }
    }

    private readonly record struct ParameterBinding(int Value) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new Number());
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Number(Scope.Read<ParameterBinding>(Scope.Entity).Value)));
            return default;
        }
    }

    private readonly record struct CalculationView(Action Content) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Content();
            return default;
        }
    }

    private readonly record struct StoredMemo(State<int> Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            State<int> number = Number;
            Scope.World.Add(Scope.Entity, new MemoValue(Scope.Memo(() => number.Value * 2)));
            return default;
        }
    }

    private readonly record struct SignBinding(State<int> Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            State<int> number = Number;
            Memo<bool> positive = Scope.Memo(() => number.Value > 0);
            Scope.World.Add(Scope.Entity, new Text("initial"));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Text(positive.Value ? "positive" : "negative")));
            return default;
        }
    }

    private readonly record struct ConditionalBinding(State<bool> Enabled, State<int> Number, Entity Target)
        : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            State<bool> enabled = Enabled;
            State<int> number = Number;
            Entity target = Target;
            Scope.Compute(() =>
            {
                if (enabled.Value)
                    Scope.Compute(() => Scope.World.Replace(target, new Number(number.Value)));
                else
                    Scope.World.Replace(target, new Number(-1));
            });
            return default;
        }
    }

    private readonly record struct NumberBinding(State<int> Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity child = Scope.View(new TextView("built"), 1);
            Scope.World.Replace(child, new Text("built"));
            State<int> number = Number;
            if (!Scope.World.Has<Number>(Scope.Entity))
                Scope.World.Add(Scope.Entity, new Number());
            Scope.Compute(() => Scope.World.Replace(Scope.Entity, new Number(number.Value)));
            return default;
        }
    }

    private readonly record struct TextView(string Text) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new Text(Text));
            return default;
        }
    }

    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent;
    private readonly record struct Text(string Value) : IComponent;
    private readonly record struct MemoValue(Memo<int> Value) : IComponent;
}
