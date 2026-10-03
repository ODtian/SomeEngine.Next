using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed class FirstBuildTests
{
    [Fact]
    public void NativeConsumerBuildsAndUpdatesMixedStorageWithCompletedAwaits() => FirstBuildChecks.Verify();

    [Fact]
    public void RootBuildCreatesChildrenInDeclarationOrderWithTheirParametersAndTypes()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThreeChildren());

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(3, children.Count);
        Assert.Equal("first", frames.World.Read<Label>(children[0]).Value);
        Assert.Equal(23, frames.World.Read<Number>(children[1]).Value);
        Assert.Equal("third", frames.World.Read<Label>(children[2]).Value);
        Assert.Equal(frames.World.Read<ViewType>(children[0]).Id, frames.World.Read<ViewType>(children[2]).Id);
        Assert.NotEqual(frames.World.Read<ViewType>(children[0]).Id, frames.World.Read<ViewType>(children[1]).Id);
    }

    [Fact]
    public void SparseRootAndMixedChildrenBuildTheirParametersWithinOneFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new SparseGroup("group"));

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Equal(3, children.Count);
        Assert.Equal("group", frames.World.ReadSparse<SparseGroup>(root).Name);
        Assert.Equal("group", frames.World.Read<DisplayValue>(root).Value);
        Assert.Equal("branch", frames.World.Read<Branch>(children[0]).Name);
        Assert.Equal("sparse", frames.World.ReadSparse<SparseLabel>(children[1]).Value);
        Assert.Equal("sparse", frames.World.Read<DisplayValue>(children[1]).Value);
        Assert.Equal(7, frames.World.Read<Number>(children[2]).Value);
        var grandchildren = Hierarchy<ElementTree>.GetChildren(frames.World, children[0]);
        Assert.Single(grandchildren);
        Assert.Equal("leaf", frames.World.ReadSparse<SparseLabel>(grandchildren[0]).Value);
        Assert.Equal("leaf", frames.World.Read<DisplayValue>(grandchildren[0]).Value);
    }

    [Fact]
    public void CompletedAsyncBuildCreatesChildrenWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CompletedAsyncView("ready"));

        frames.Frame();

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Assert.Single(children);
        Assert.Equal("ready", frames.World.Read<Label>(children[0]).Value);
    }

    [Fact]
    public void RootParametersWrittenBeforeItsFirstFrameSupplyTheBuild()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NamedGroup("before"));
        Assert.Equal("before", frames.World.Read<NamedGroup>(root).Name);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(root, new NamedGroup("after"));

        frames.Frame();

        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("after", frames.World.Read<Label>(child).Value);
    }

    [Fact]
    public void RootsAttachToTheirParentInSpawnOrder()
    {
        using var frames = new HeadlessFrameDriver();
        Entity parent = frames.World.CreateEntity();
        Entity first = Views.Spawn(frames.World, parent, new Label("first"));
        Entity second = Views.Spawn(frames.World, parent, new Number(29));

        frames.Frame();

        Assert.Equal(new[] { first, second }, Hierarchy<ElementTree>.GetChildren(frames.World, parent).ToArray());
        Assert.Equal(parent, Hierarchy<ElementTree>.GetParent(frames.World, first));
        Assert.Equal(parent, Hierarchy<ElementTree>.GetParent(frames.World, second));
    }

    [Fact]
    public void WorldsBuildIndependentTrees()
    {
        using var first = new HeadlessFrameDriver();
        using var second = new HeadlessFrameDriver();
        Entity firstRoot = Views.Spawn(first.World, Entity.Null, new NamedGroup("first world"));
        Entity secondRoot = Views.Spawn(second.World, Entity.Null, new NamedGroup("second world"));

        first.Frame();

        Assert.Empty(Hierarchy<ElementTree>.GetChildren(second.World, secondRoot));
        Entity firstChild = Assert.Single(Hierarchy<ElementTree>.GetChildren(first.World, firstRoot));
        Assert.Equal("first world", first.World.Read<Label>(firstChild).Value);

        second.Frame();

        Entity secondChild = Assert.Single(Hierarchy<ElementTree>.GetChildren(second.World, secondRoot));
        Assert.Equal("second world", second.World.Read<Label>(secondChild).Value);
    }

    [Fact]
    public void OnceViewBuildsItsFirstContentAndRetainsItAcrossFrames()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceGroup("once"));

        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal("once", frames.World.Read<Label>(child).Value);
    }

#if DEBUG
    [Fact]
    public void ViewDeclarationOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));

        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuspendedBuildRestoresTheSurroundingScopeAfterEachFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource();
        Views.Spawn(frames.World, Entity.Null, new SuspendedView(completion.Task));

        frames.Frame();
        var scopeError = Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
        Assert.Contains("active view build", scopeError.Message, StringComparison.OrdinalIgnoreCase);
        completion.SetResult();
        frames.Frame();
        scopeError = Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
        Assert.Contains("active view build", scopeError.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    private readonly record struct ThreeChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Label("first"));
            Scope.View(new Number(23));
            Scope.View(new Label("third"));
            return default;
        }
    }

    private readonly record struct Label(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct NamedGroup(string Name) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Label(Name));
            return default;
        }
    }

    private readonly record struct OnceGroup(string Name) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.View(new Label(Name));
            return default;
        }
    }

    private readonly record struct SuspendedView(Task Completion) : IView, IComponent
    {
        public async BuildTask Build() => await Completion;
    }

    private readonly record struct SparseGroup(string Name) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Branch("branch"));
            Scope.View(new SparseLabel("sparse"));
            Scope.View(new Number(7));
            Scope.World.Add(Scope.Entity, new DisplayValue(Name));
            return default;
        }
    }

    private readonly record struct Branch(string Name) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new SparseLabel("leaf"));
            return default;
        }
    }

    private readonly record struct SparseLabel(string Value) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new DisplayValue(Value));
            return default;
        }
    }

    private readonly record struct DisplayValue(string Value) : IComponent;

    private readonly record struct Number(int Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct CompletedAsyncView(string Value) : IView, IComponent
    {
        public async BuildTask Build()
        {
            await Task.CompletedTask;
            await ValueTask.CompletedTask;
            Scope.View(new Label(Value));
        }
    }
}
