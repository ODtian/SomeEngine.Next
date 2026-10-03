using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed class ReadDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentWritesUpdateTheDisplayOnTheNextFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        uint sourceVersion = sparse
            ? frames.World.SparseWriteVersion<SparseValue>(source)
            : frames.World.WriteVersion<Value>(source);
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source, sparse));

        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(sourceVersion, sparse
            ? frames.World.SparseWriteVersion<SparseValue>(source)
            : frames.World.WriteVersion<Value>(source));
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));

        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
            frames.World.Replace(source, new Value(7));
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
        displayVersion = frames.World.WriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedComponentChangesSupplyNewParentParametersToTheChildWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueGroup(source, sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));

        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
            frames.World.Replace(source, new Value(7));
        frames.Frame();

        Assert.Equal("30:3;70:7", frames.World.Read<DisplayText>(child).Value);
        Assert.Equal("30:3;70:7", frames.World.Read<DisplayText>(root).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentCanReleaseAnInvalidatedChildAndRetainItsEmptyDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity visibility = frames.World.CreateEntity(new Visibility(true));
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConditionalGroup(visibility, source, sparse));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(visibility, new Visibility(false));
        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
            frames.World.Replace(source, new Value(7));

        frames.Frame();

        Assert.False(frames.World.IsAlive(child));
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("shown;empty", frames.World.Read<DisplayText>(root).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(11));
        else
            frames.World.Replace(source, new Value(11));
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));
        Assert.Equal("shown;empty", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedSelectionKeepsOnlyTheSelectedComponentsDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity selection = frames.World.CreateEntity(new Visibility(true));
        Entity first = frames.World.CreateEntity();
        Entity second = frames.World.CreateEntity();
        if (sparse)
        {
            frames.World.AddSparse(first, new SparseValue(3));
            frames.World.AddSparse(second, new SparseValue(5));
        }
        else
        {
            frames.World.Add(first, new Value(3));
            frames.World.Add(second, new Value(5));
        }
        Entity root = Views.Spawn(frames.World, Entity.Null, new SelectedDisplay(selection, first, second, sparse));
        frames.Frame();
        frames.World.Replace(selection, new Visibility(false));

        frames.Frame();
        Assert.Equal("3;5", frames.World.Read<DisplayText>(root).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.ReplaceSparse(first, new SparseValue(7));
        else
            frames.World.Replace(first, new Value(7));
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));

        if (sparse)
            frames.World.ReplaceSparse(second, new SparseValue(9));
        else
            frames.World.Replace(second, new Value(9));
        frames.Frame();
        Assert.Equal("3;5;9", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceBuildReadsKeepTheFirstDisplayAfterComponentWrites(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceValueDisplay(source, sparse));
        frames.Frame();
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
            frames.World.Replace(source, new Value(7));

        frames.Frame();
        frames.Frame();

        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));
    }

    [Fact]
    public void RepeatedReadsKeepEachEntityAndComponentInTheDisplayedValues()
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.CreateEntity(new Value(3));
        frames.World.AddSparse(first, new SparseValue(5));
        Entity second = frames.World.CreateEntity(new Value(7));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CombinedDisplay(first, second));
        frames.Frame();
        Assert.Equal("3+3+5+7", frames.World.Read<DisplayText>(root).Value);

        frames.World.Replace(first, new Value(11));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7", frames.World.Read<DisplayText>(root).Value);
        frames.World.ReplaceSparse(first, new SparseValue(13));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7;11+11+13+7", frames.World.Read<DisplayText>(root).Value);
        frames.World.Replace(second, new Value(17));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7;11+11+13+7;11+11+13+17", frames.World.Read<DisplayText>(root).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(root));
    }

    [Fact]
    public void NestedBuildsKeepTheirReadsWithTheirOwnDisplays()
    {
        using var frames = new HeadlessFrameDriver();
        Entity parentSource = frames.World.CreateEntity(new Value(3));
        Entity childSource = frames.World.CreateEntity(new Value(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SeparateGroup(parentSource, childSource));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint parentDisplayVersion = frames.World.WriteVersion<DisplayText>(root);

        frames.World.Replace(childSource, new Value(7));
        frames.Frame();
        Assert.Equal("5;7", frames.World.Read<DisplayText>(child).Value);
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(parentDisplayVersion, frames.World.WriteVersion<DisplayText>(root));

        uint childDisplayVersion = frames.World.WriteVersion<DisplayText>(child);
        frames.World.Replace(parentSource, new Value(11));
        frames.Frame();
        Assert.Equal("3;11", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(childDisplayVersion, frames.World.WriteVersion<DisplayText>(child));

        frames.World.Replace(parentSource, new Value(13));
        frames.World.Replace(childSource, new Value(17));
        frames.Frame();
        Assert.Equal("3;11;13", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal("5;7;17", frames.World.Read<DisplayText>(child).Value);
    }

    private readonly record struct SeparateGroup(Entity ParentSource, Entity ChildSource) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ValueDisplay(ChildSource, Sparse: false));
            AppendDisplay(Scope.Read<Value>(ParentSource).Number.ToString());
            return default;
        }
    }

#if DEBUG
    [Fact]
    public void ReadOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Scope.Read<Value>(Entity.Null));
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    private readonly record struct CombinedDisplay(Entity First, Entity Second) : IView, IComponent
    {
        public BuildTask Build()
        {
            int first = Scope.Read<Value>(First).Number;
            int firstAgain = Scope.Read<Value>(First).Number;
            int sparse = Scope.Read<SparseValue>(First).Number;
            int second = Scope.Read<Value>(Second).Number;
            AppendDisplay($"{first}+{firstAgain}+{sparse}+{second}");
            return default;
        }
    }

    private readonly record struct OnceValueDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            AppendDisplay(ReadValue(Source, Sparse).ToString());
            return default;
        }
    }

    private readonly record struct SelectedDisplay(Entity Selection, Entity First, Entity Second, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity source = Scope.Read<Visibility>(Selection).Shown ? First : Second;
            AppendDisplay(ReadValue(source, Sparse).ToString());
            return default;
        }
    }

    private readonly record struct ConditionalGroup(Entity Visibility, Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Scope.Read<Visibility>(Visibility).Shown)
            {
                Scope.View(new ValueDisplay(Source, Sparse));
                AppendDisplay("shown");
            }
            else
            {
                AppendDisplay("empty");
            }
            return default;
        }
    }

    private readonly record struct ValueGroup(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = ReadValue(Source, Sparse);
            Entity child = Scope.View(new ParameterDisplay(Source, Sparse, value * 10));
            WriteDisplay(Scope.World.Read<DisplayText>(child).Value);
            return default;
        }
    }

    private readonly record struct ParameterDisplay(Entity Source, bool Sparse, int Parameter) : IView, IComponent
    {
        public BuildTask Build()
        {
            AppendDisplay($"{Parameter}:{ReadValue(Source, Sparse)}");
            return default;
        }
    }

    private static int ReadValue(Entity source, bool sparse) => sparse
        ? Scope.Read<SparseValue>(source).Number
        : Scope.Read<Value>(source).Number;

    private readonly record struct ValueDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value;
            if (Sparse)
            {
                ref readonly SparseValue data = ref Scope.Read<SparseValue>(Source);
                value = data.Number;
            }
            else
            {
                ref readonly Value data = ref Scope.Read<Value>(Source);
                value = data.Number;
            }
            AppendDisplay(value.ToString());
            return default;
        }
    }

    private static void AppendDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            text = $"{Scope.World.Read<DisplayText>(Scope.Entity).Value};{text}";
        WriteDisplay(text);
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct Value(int Number) : IComponent;
    private readonly record struct Visibility(bool Shown) : IComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent;
    private readonly record struct DisplayText(string Value) : IComponent;
}
