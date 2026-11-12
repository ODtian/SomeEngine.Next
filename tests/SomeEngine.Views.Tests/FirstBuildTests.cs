using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class FirstBuildTests
{
    [Fact]
    public void NativeConsumerBuildsAndUpdatesMixedStorageWithCompletedAwaits() => FirstBuildChecks.Verify();

    [Fact]
    public void RootBuildCreatesChildrenInDeclarationOrderWithTheirParametersAndTypes()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThreeChildren());

        frames.Frame();

        var children = ElementChildren.Read(frames.World, root);
        Assert.Equal(3, children.Length);
        Assert.Equal("first", frames.World.ReadComponentData<Label>(children[0]).Value);
        Assert.Equal(23, frames.World.ReadComponentData<Number>(children[1]).Value);
        Assert.Equal("third", frames.World.ReadComponentData<Label>(children[2]).Value);
        Assert.Equal(frames.World.ReadComponentData<ViewType>(children[0]).Id, frames.World.ReadComponentData<ViewType>(children[2]).Id);
        Assert.NotEqual(frames.World.ReadComponentData<ViewType>(children[0]).Id, frames.World.ReadComponentData<ViewType>(children[1]).Id);
    }

    [Fact]
    public void SparseRootAndMixedChildrenBuildTheirParametersWithinOneFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new SparseGroup("group"));

        frames.Frame();

        var children = ElementChildren.Read(frames.World, root);
        Assert.Equal(3, children.Length);
        Assert.Equal("group", frames.World.ReadComponentData<SparseGroup>(root).Name);
        Assert.Equal("group", frames.World.ReadComponentData<DisplayValue>(root).Value);
        Assert.Equal("branch", frames.World.ReadComponentData<Branch>(children[0]).Name);
        Assert.Equal("sparse", frames.World.ReadComponentData<SparseLabel>(children[1]).Value);
        Assert.Equal("sparse", frames.World.ReadComponentData<DisplayValue>(children[1]).Value);
        Assert.Equal(7, frames.World.ReadComponentData<Number>(children[2]).Value);
        var grandchildren = ElementChildren.Read(frames.World, children[0]);
        Assert.Single(grandchildren);
        Assert.Equal("leaf", frames.World.ReadComponentData<SparseLabel>(grandchildren[0]).Value);
        Assert.Equal("leaf", frames.World.ReadComponentData<DisplayValue>(grandchildren[0]).Value);
    }

    [Fact]
    public void CompletedAsyncBuildCreatesChildrenWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CompletedAsyncView("ready"));

        frames.Frame();

        var children = ElementChildren.Read(frames.World, root);
        Assert.Single(children);
        Assert.Equal("ready", frames.World.ReadComponentData<Label>(children[0]).Value);
    }

    [Fact]
    public void RootParametersWrittenBeforeItsFirstFrameSupplyTheBuild()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NamedGroup("before"));
        Assert.Equal("before", frames.World.ReadComponentData<NamedGroup>(root).Name);
        Assert.Empty(ElementChildren.Read(frames.World, root));
        frames.World.Set(root, new NamedGroup("after"));

        frames.Frame();

        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal("after", frames.World.ReadComponentData<Label>(child).Value);
    }

    [Fact]
    public void RootsAttachToTheirParentInSpawnOrder()
    {
        using var frames = new HeadlessFrameDriver();
        Entity parent = frames.World.Create();
        Entity first = Views.Spawn(frames.World, parent, new Label("first"));
        Entity second = Views.Spawn(frames.World, parent, new Number(29));

        frames.Frame();

        Assert.Equal(new[] { first, second }, ElementChildren.Read(frames.World, parent));
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

        Assert.Empty(ElementChildren.Read(second.World, secondRoot));
        Entity firstChild = Assert.Single(ElementChildren.Read(first.World, firstRoot));
        Assert.Equal("first world", first.World.ReadComponentData<Label>(firstChild).Value);

        second.Frame();

        Entity secondChild = Assert.Single(ElementChildren.Read(second.World, secondRoot));
        Assert.Equal("second world", second.World.ReadComponentData<Label>(secondChild).Value);
    }

    [Fact]
    public void OnceViewBuildsItsFirstContentAndRetainsItAcrossFrames()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceGroup("once"));

        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        frames.Frame();

        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal("once", frames.World.ReadComponentData<Label>(child).Value);
    }

#if DEBUG
    [Fact]
    public void ViewDeclarationOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
    }

    [Fact]
    public void SuspendedBuildRestoresTheSurroundingScopeAfterEachFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var completion = new TaskCompletionSource();
        Entity root = Views.Spawn(frames.World, Entity.Null, new SuspendedView(completion.Task));

        frames.Frame();
        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
        completion.SetResult();
        frames.Frame();
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
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

    private readonly record struct Number(int Value) : IView, ITrackedComponent
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
