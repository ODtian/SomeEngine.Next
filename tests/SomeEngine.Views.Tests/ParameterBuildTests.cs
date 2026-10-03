using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed class ParameterBuildTests
{
    [Fact]
    public void ParentFirstBuildUpdatesAnExistingChildDisplayWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new HistoryGroup(7));
        Entity child = Views.Spawn(frames.World, root, new HistoryDisplay(3));

        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal("7", frames.World.Read<DisplayedValues>(child).Text);
        Assert.Equal("7", frames.World.Read<DisplayedValues>(root).Text);
        uint displayVersion = frames.World.WriteVersion<DisplayedValues>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayedValues>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalRootParametersUpdateTheDisplayOnTheNextFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = sparse
            ? Views.Spawn(frames.World, Entity.Null, new SparseDisplay(3))
            : Views.Spawn(frames.World, Entity.Null, new Display(3));
        frames.Frame();
        if (sparse)
            frames.World.ReplaceSparse(root, new SparseDisplay(7));
        else
            frames.World.Replace(root, new Display(7));

        Assert.Equal(3, frames.World.Read<DisplayValue>(root).Value);
        frames.Frame();
        Assert.Equal(7, frames.World.Read<DisplayValue>(root).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(root);
        frames.Frame();

        Assert.Equal(7, frames.World.Read<DisplayValue>(root).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRerunKeepsEqualChildParametersAndDisplayVersions(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new DisplayGroup(3, sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parameterVersion = sparse
            ? frames.World.SparseWriteVersion<SparseDisplay>(child)
            : frames.World.WriteVersion<Display>(child);
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(child);
        uint typeVersion = sparse
            ? frames.World.ComponentWriteVersion<SparseDisplay>()
            : frames.World.ComponentWriteVersion<Display>();

        frames.World.Replace(root, new DisplayGroup(3, sparse));
        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(parameterVersion, sparse
            ? frames.World.SparseWriteVersion<SparseDisplay>(child)
            : frames.World.WriteVersion<Display>(child));
        Assert.Equal(3, frames.World.Read<DisplayValue>(child).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(child));
        Assert.Equal(typeVersion, sparse
            ? frames.World.ComponentWriteVersion<SparseDisplay>()
            : frames.World.ComponentWriteVersion<Display>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRerunBuildsChangedChildParametersWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new DisplayGroup(3, sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parameterVersion = sparse
            ? frames.World.SparseWriteVersion<SparseDisplay>(child)
            : frames.World.WriteVersion<Display>(child);
        frames.World.Replace(root, new DisplayGroup(7, sparse));

        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.NotEqual(parameterVersion, sparse
            ? frames.World.SparseWriteVersion<SparseDisplay>(child)
            : frames.World.WriteVersion<Display>(child));
        Assert.Equal(7, frames.World.Read<DisplayValue>(child).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(child));
    }

    [Fact]
    public void ParentParametersSupplyTheChildDisplayBeforeItsFrameUpdate()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new HistoryGroup(3));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(child, new HistoryDisplay(99));
        frames.World.Replace(root, new HistoryGroup(7));

        frames.Frame();

        Assert.Equal("3,7", frames.World.Read<DisplayedValues>(child).Text);
        Assert.Equal("3,7", frames.World.Read<DisplayedValues>(root).Text);
        uint displayVersion = frames.World.WriteVersion<DisplayedValues>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayedValues>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceRootParametersKeepTheFirstDisplayAfterExternalWrite(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = sparse
            ? Views.Spawn(frames.World, Entity.Null, new SparseOnceDisplay(3))
            : Views.Spawn(frames.World, Entity.Null, new OnceDisplay(3));
        frames.Frame();
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(root);
        if (sparse)
            frames.World.ReplaceSparse(root, new SparseOnceDisplay(7));
        else
            frames.World.Replace(root, new OnceDisplay(7));

        frames.Frame();
        frames.Frame();

        Assert.Equal(7, sparse
            ? frames.World.ReadSparse<SparseOnceDisplay>(root).Value
            : frames.World.Read<OnceDisplay>(root).Value);
        Assert.Equal(3, frames.World.Read<DisplayValue>(root).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRerunUpdatesOnceChildParametersAndKeepsTheFirstDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new DisplayGroup(3, sparse, Once: true));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parameterVersion = sparse
            ? frames.World.SparseWriteVersion<SparseOnceDisplay>(child)
            : frames.World.WriteVersion<OnceDisplay>(child);
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(child);
        frames.World.Replace(root, new DisplayGroup(7, sparse, Once: true));

        frames.Frame();
        frames.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(7, sparse
            ? frames.World.ReadSparse<SparseOnceDisplay>(child).Value
            : frames.World.Read<OnceDisplay>(child).Value);
        Assert.NotEqual(parameterVersion, sparse
            ? frames.World.SparseWriteVersion<SparseOnceDisplay>(child)
            : frames.World.WriteVersion<OnceDisplay>(child));
        Assert.Equal(3, frames.World.Read<DisplayValue>(child).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalChildParametersUpdateItsDisplayWhileParentParametersStayEqual(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new DisplayGroup(3, sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        if (sparse)
            frames.World.ReplaceSparse(child, new SparseDisplay(7));
        else
            frames.World.Replace(child, new Display(7));

        frames.Frame();

        Assert.Equal(7, frames.World.Read<DisplayValue>(child).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualParametersKeepTheLatestValuesSelectedByCustomEquality(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new CallbackGroup(new CallbackValue(3, static () => 11), sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parameterVersion = sparse
            ? frames.World.SparseWriteVersion<SparseCallbackDisplay>(child)
            : frames.World.WriteVersion<CallbackDisplay>(child);
        uint typeVersion = sparse
            ? frames.World.ComponentWriteVersion<SparseCallbackDisplay>()
            : frames.World.ComponentWriteVersion<CallbackDisplay>();
        uint displayVersion = frames.World.WriteVersion<DisplayValue>(child);
        frames.World.Replace(root, new CallbackGroup(new CallbackValue(3, static () => 29), sparse));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        CallbackValue parameters = sparse
            ? frames.World.ReadSparse<SparseCallbackDisplay>(child).Parameters
            : frames.World.Read<CallbackDisplay>(child).Parameters;
        Assert.Equal(29, parameters.NumberCallback());
        Assert.Equal(3, frames.World.Read<DisplayValue>(child).Value);
        Assert.Equal(parameterVersion, sparse
            ? frames.World.SparseWriteVersion<SparseCallbackDisplay>(child)
            : frames.World.WriteVersion<CallbackDisplay>(child));
        Assert.Equal(typeVersion, sparse
            ? frames.World.ComponentWriteVersion<SparseCallbackDisplay>()
            : frames.World.ComponentWriteVersion<CallbackDisplay>());
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayValue>(child));
    }

    [Fact]
    public void NestedParentRerunKeepsEachChildAtItsDeclarationIndex()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedGroup(3, 5));
        frames.Frame();
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Entity branch = children[0];
        Entity sibling = children[1];
        Entity grandchild = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, branch));
        uint grandchildVersion = frames.World.WriteVersion<DisplayValue>(grandchild);
        frames.World.Replace(root, new NestedGroup(3, 7));

        frames.Frame();

        Assert.Equal(new[] { branch, sibling }, Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        Assert.Equal(grandchild, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, branch)));
        Assert.Equal(3, frames.World.Read<DisplayValue>(grandchild).Value);
        Assert.Equal(grandchildVersion, frames.World.WriteVersion<DisplayValue>(grandchild));
        Assert.Equal(7, frames.World.Read<DisplayValue>(sibling).Value);
    }

    private readonly record struct NestedGroup(int First, int Second) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new DisplayGroup(First, Sparse: false));
            Scope.View(new Display(Second));
            return default;
        }
    }

    private readonly record struct CallbackValue(int Value, Func<int> NumberCallback)
    {
        public bool Equals(CallbackValue other) => Value == other.Value;
        public override int GetHashCode() => Value;
    }

    private readonly record struct CallbackGroup(CallbackValue Parameters, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Sparse)
                Scope.View(new SparseCallbackDisplay(Parameters));
            else
                Scope.View(new CallbackDisplay(Parameters));
            return default;
        }
    }

    private readonly record struct CallbackDisplay(CallbackValue Parameters) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Parameters.Value);
            return default;
        }
    }

    private readonly record struct SparseCallbackDisplay(CallbackValue Parameters) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Parameters.Value);
            return default;
        }
    }

    private readonly record struct HistoryGroup(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity child = Scope.View(new HistoryDisplay(Value));
            WriteDisplayedValues(Scope.World.Read<DisplayedValues>(child).Text);
            return default;
        }
    }

    private readonly record struct HistoryDisplay(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            string text = Scope.World.Has<DisplayedValues>(Scope.Entity)
                ? $"{Scope.World.Read<DisplayedValues>(Scope.Entity).Text},{Value}"
                : Value.ToString();
            WriteDisplayedValues(text);
            return default;
        }
    }

    private static void WriteDisplayedValues(string text)
    {
        if (Scope.World.Has<DisplayedValues>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayedValues(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayedValues(text));
    }

    private readonly record struct DisplayedValues(string Text) : IComponent;

    private readonly record struct DisplayGroup(int Value, bool Sparse, bool Once = false) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Once && Sparse)
                Scope.View(new SparseOnceDisplay(Value));
            else if (Once)
                Scope.View(new OnceDisplay(Value));
            else if (Sparse)
                Scope.View(new SparseDisplay(Value));
            else
                Scope.View(new Display(Value));
            return default;
        }
    }

    private readonly record struct SparseDisplay(int Value) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Value);
            return default;
        }
    }

    private readonly record struct Display(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Value);
            return default;
        }
    }

    private readonly record struct OnceDisplay(int Value) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            WriteDisplay(Value);
            return default;
        }
    }

    private readonly record struct SparseOnceDisplay(int Value) : IView, ISparseComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            WriteDisplay(Value);
            return default;
        }
    }

    private static void WriteDisplay(int value)
    {
        if (Scope.World.Has<DisplayValue>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayValue(value));
        else
            Scope.World.Add(Scope.Entity, new DisplayValue(value));
    }

    private readonly record struct DisplayValue(int Value) : IComponent;
}
