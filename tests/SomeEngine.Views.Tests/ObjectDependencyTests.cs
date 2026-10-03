using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ObjectDependencyTests
{
    [Fact]
    public void CompletingAnObservedWriteRefreshesTheDisplayWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver(iterationLimit: 2);
        var source = new NumberData(3) { Version = 1 };
        Entity root = Views.Spawn(frames.World, Entity.Null, new CompletingNumberDisplay(source));

        frames.Frame();

        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void DependOutsideAComputationKeepsTheDataAvailable()
    {
        var source = new NumberData(3);

        Scope.Depend(source);
        source.AdvanceShared();
        Scope.Depend(source);

        Assert.Equal(3, source.Number);
        Assert.Equal(2u, source.Version);
    }

    [Fact]
    public async Task SharedAdvancesRetainEveryConcurrentVersionChange()
    {
        var source = new NumberData(3);
        Task[] writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < 2500; index++)
                source.AdvanceShared();
        })).ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(20000u, source.Version);
        source.Version = uint.MaxValue - 1;
        source.AdvanceShared();
        Assert.Equal(0u, source.Version);
        source.Version = uint.MaxValue - 1;
        source.Advance();
        Assert.Equal(0u, source.Version);
    }

    [Fact]
    public void AnActiveObjectChangeUpdatesTheDisplayAndRecoversAfterCompletion()
    {
        using var frames = new HeadlessFrameDriver(iterationLimit: 2);
        var source = new NumberData(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberDisplay(source));
        frames.Frame();

        source.Version = 1;
        source.Number = 7;
#if DEBUG
        var firstError = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("iteration limit of 2", firstError.Message, StringComparison.OrdinalIgnoreCase);
#else
        frames.Frame();
#endif
        Assert.EndsWith(";7", frames.World.Read<DisplayText>(root).Value);
        source.Number = 9;
#if DEBUG
        Assert.Throws<InvalidOperationException>(frames.Frame);
#else
        frames.Frame();
#endif

        Assert.EndsWith(";9", frames.World.Read<DisplayText>(root).Value);
        source.Number = 11;
        source.Version = 2;
        frames.Frame();
        Assert.EndsWith(";11", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void AChangedSelectionMakesTheDisplayFollowTheSelectedObject()
    {
        using var frames = new HeadlessFrameDriver();
        var selection = new State<bool>(true);
        var first = new NumberData(3);
        var second = new NumberData(5);
        Entity root = Views.Spawn(frames.World, Entity.Null, new SelectedNumberDisplay(selection, first, second));
        frames.Frame();

        selection.Value = false;
        frames.Frame();
        Assert.Equal("3;5", frames.World.Read<DisplayText>(root).Value);
        first.Number = 7;
        first.Advance();
        frames.Frame();
        Assert.Equal("3;5", frames.World.Read<DisplayText>(root).Value);
        second.Number = 11;
        second.Advance();
        frames.Frame();
        Assert.Equal("3;5;11", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void NestedViewsKeepTheirObjectReadsWithTheirOwnDisplays()
    {
        using var frames = new HeadlessFrameDriver();
        var parentSource = new NumberData(3);
        var childSource = new NumberData(5);
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberGroup(parentSource, childSource));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));

        childSource.Number = 7;
        childSource.AdvanceShared();
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal("5;7", frames.World.Read<DisplayText>(child).Value);
        parentSource.Number = 11;
        parentSource.Advance();
        frames.Frame();
        Assert.Equal("3;11", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal("5;7", frames.World.Read<DisplayText>(child).Value);
    }

    [Fact]
    public void OnceBuildsRetainTheirFirstObjectDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        var source = new NumberData(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceNumberDisplay(source));
        frames.Frame();

        source.Number = 7;
        source.Advance();
        frames.Frame();
        frames.Frame();

        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void AViewCanReadManyObjectsAcrossCollectionAndLaterChanges()
    {
        using var frames = new HeadlessFrameDriver();
        NumberData[] sources = Enumerable.Range(1, 32).Select(value => new NumberData(value)).ToArray();
        Entity root = Views.Spawn(frames.World, Entity.Null, new TotalDisplay(sources));
        frames.Frame();
        Assert.Equal("528", frames.World.Read<DisplayText>(root).Value);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        sources[31].Number = 37;
        sources[31].Advance();
        frames.Frame();
        Assert.Equal("528;533", frames.World.Read<DisplayText>(root).Value);
        sources[0].Number = 7;
        sources[0].AdvanceShared();
        frames.Frame();
        Assert.Equal("528;533;539", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void AdvancingACustomDataSourceUpdatesItsDisplayOnTheNextFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var source = new NumberData(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberDisplay(source));
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        source.Number = 7;
        source.Advance();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
    }

    private sealed class NumberData(int number) : VersionCell
    {
        internal int Number = number;
    }

    private readonly record struct NumberDisplay(NumberData Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Depend(Source);
            AppendDisplay(Source.Number.ToString());
            return default;
        }
    }

    private readonly record struct CompletingNumberDisplay(NumberData Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Depend(Source);
            int number = Source.Number;
            Source.Number = 7;
            Volatile.Write(ref Source.Version, 2);
            AppendDisplay(number.ToString());
            return default;
        }
    }

    private readonly record struct TotalDisplay(NumberData[] Sources) : IView, IComponent
    {
        public BuildTask Build()
        {
            int total = 0;
            foreach (NumberData source in Sources)
            {
                Scope.Depend(source);
                total += source.Number;
            }
            AppendDisplay(total.ToString());
            return default;
        }
    }

    private readonly record struct OnceNumberDisplay(NumberData Source) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.Depend(Source);
            AppendDisplay(Source.Number.ToString());
            return default;
        }
    }

    private readonly record struct NumberGroup(NumberData ParentSource, NumberData ChildSource) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new NumberDisplay(ChildSource));
            Scope.Depend(ParentSource);
            AppendDisplay(ParentSource.Number.ToString());
            return default;
        }
    }

    private readonly record struct SelectedNumberDisplay(State<bool> Selection, NumberData First, NumberData Second) : IView, IComponent
    {
        public BuildTask Build()
        {
            NumberData source = Selection.Value ? First : Second;
            Scope.Depend(source);
            AppendDisplay(source.Number.ToString());
            return default;
        }
    }

    private static void AppendDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
        {
            text = $"{Scope.World.Read<DisplayText>(Scope.Entity).Value};{text}";
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        }
        else
        {
            Scope.World.Add(Scope.Entity, new DisplayText(text));
        }
    }

    private readonly record struct DisplayText(string Value) : IComponent;
}
