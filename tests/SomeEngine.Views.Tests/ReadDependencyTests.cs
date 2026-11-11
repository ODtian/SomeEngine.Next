using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ReadDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentTypeChangesUpdateOnlyTheirDependentDisplays(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity tableSource = frames.World.Create(new Value(3));
        Entity sparseSource = frames.World.Create();
        frames.World.SetSparse(sparseSource, new SparseValue(5));
        Entity tableDisplay = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(tableSource, Sparse: false));
        Entity sparseDisplay = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(sparseSource, Sparse: true));
        frames.Frame();
        frames.Frame();

        if (sparse)
            frames.World.SetSparse(sparseSource, new SparseValue(7));
        else
            frames.World.Set(tableSource, new Value(11));
        frames.Frame();

        Assert.Equal(sparse ? "3" : "3;11", frames.World.ReadComponentData<DisplayText>(tableDisplay).Value);
        Assert.Equal(sparse ? "5;7" : "5", frames.World.ReadComponentData<DisplayText>(sparseDisplay).Value);
        ulong tableDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(tableDisplay);
        ulong sparseDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(sparseDisplay);
        frames.Frame();
        Assert.Equal(tableDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(tableDisplay));
        Assert.Equal(sparseDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(sparseDisplay));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentWritesAfterBuildingAChildUpdateItsDisplayWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity trigger = frames.World.Create(new Value(3));
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(5));
        else
            frames.World.Add(source, new OtherValue(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new RewritingGroup(trigger, source, sparse));
        frames.Frame();
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));

        frames.World.Set(trigger, new Value(7));
        frames.Frame();

        Assert.Equal("30:3+5;70:7+5;70:7+11", frames.World.ReadComponentData<DisplayText>(child).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentWritesAfterFirstChildBuildUpdateTheDisplayWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new InitialWriteGroup(source, sparse));
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal("3;7", frames.World.ReadComponentData<DisplayText>(child).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(child);

        frames.Frame();

        Assert.Equal("3;7", frames.World.ReadComponentData<DisplayText>(child).Value);
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentChangesKeepEachWorldsDisplayIndependent(bool sparse)
    {
        using var first = new HeadlessFrameDriver();
        using var second = new HeadlessFrameDriver();
        Entity firstSource = first.World.Create();
        Entity secondSource = second.World.Create();
        if (sparse)
        {
            first.World.SetSparse(firstSource, new SparseValue(3));
            second.World.SetSparse(secondSource, new SparseValue(5));
        }
        else
        {
            first.World.Add(firstSource, new Value(3));
            second.World.Add(secondSource, new Value(5));
        }
        Entity firstDisplay = Views.Spawn(first.World, Entity.Null, new ValueDisplay(firstSource, sparse));
        Entity secondDisplay = Views.Spawn(second.World, Entity.Null, new ValueDisplay(secondSource, sparse));
        first.Frame();
        second.Frame();
        first.Frame();
        second.Frame();

        if (sparse)
            first.World.SetSparse(firstSource, new SparseValue(7));
        else
            first.World.Set(firstSource, new Value(7));
        first.Frame();
        second.Frame();
        Assert.Equal("3;7", first.World.ReadComponentData<DisplayText>(firstDisplay).Value);
        Assert.Equal("5", second.World.ReadComponentData<DisplayText>(secondDisplay).Value);
        ulong firstDisplayVersion = first.World.ComponentDataWriteVersion<DisplayText>(firstDisplay);

        if (sparse)
            second.World.SetSparse(secondSource, new SparseValue(11));
        else
            second.World.Set(secondSource, new Value(11));
        second.Frame();
        first.Frame();
        Assert.Equal("5;11", second.World.ReadComponentData<DisplayText>(secondDisplay).Value);
        Assert.Equal(firstDisplayVersion, first.World.ComponentDataWriteVersion<DisplayText>(firstDisplay));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentWritesUpdateTheDisplayOnTheNextFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        ulong sourceVersion = sparse
            ? frames.World.ComponentDataWriteVersion<SparseValue>(source)
            : frames.World.ComponentDataWriteVersion<Value>(source);
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source, sparse));

        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(sourceVersion, sparse
            ? frames.World.ComponentDataWriteVersion<SparseValue>(source)
            : frames.World.ComponentDataWriteVersion<Value>(source));
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(7));
        else
            frames.World.Set(source, new Value(7));
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);

        frames.Frame();
        Assert.Equal("3;7", frames.World.ReadComponentData<DisplayText>(root).Value);
        displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedComponentChangesSupplyNewParentParametersToTheChildWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ValueGroup(source, sparse));
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));

        if (sparse)
            frames.World.SetSparse(source, new SparseValue(7));
        else
            frames.World.Set(source, new Value(7));
        frames.Frame();

        Assert.Equal("30:3;70:7", frames.World.ReadComponentData<DisplayText>(child).Value);
        Assert.Equal("30:3;70:7", frames.World.ReadComponentData<DisplayText>(root).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(child);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(child));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentCanReleaseAnInvalidatedChildAndRetainItsEmptyDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity visibility = frames.World.Create(new Visibility(true));
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConditionalGroup(visibility, source, sparse));
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        frames.World.Set(visibility, new Visibility(false));
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(7));
        else
            frames.World.Set(source, new Value(7));

        frames.Frame();

        Assert.False(frames.World.IsAlive(child) && frames.World.Has<ViewType>(child));
        Assert.Empty(ElementChildren.Read(frames.World, root));
        Assert.Equal("shown;empty", frames.World.ReadComponentData<DisplayText>(root).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(11));
        else
            frames.World.Set(source, new Value(11));
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));
        Assert.Equal("shown;empty", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedSelectionKeepsOnlyTheSelectedComponentsDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity selection = frames.World.Create(new Visibility(true));
        Entity first = frames.World.Create();
        Entity second = frames.World.Create();
        if (sparse)
        {
            frames.World.SetSparse(first, new SparseValue(3));
            frames.World.SetSparse(second, new SparseValue(5));
        }
        else
        {
            frames.World.Add(first, new Value(3));
            frames.World.Add(second, new Value(5));
        }
        Entity root = Views.Spawn(frames.World, Entity.Null, new SelectedDisplay(selection, first, second, sparse));
        frames.Frame();
        frames.World.Set(selection, new Visibility(false));

        frames.Frame();
        Assert.Equal("3;5", frames.World.ReadComponentData<DisplayText>(root).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.SetSparse(first, new SparseValue(7));
        else
            frames.World.Set(first, new Value(7));
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));

        if (sparse)
            frames.World.SetSparse(second, new SparseValue(9));
        else
            frames.World.Set(second, new Value(9));
        frames.Frame();
        Assert.Equal("3;5;9", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceBuildReadsKeepTheFirstDisplayAfterComponentWrites(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceValueDisplay(source, sparse));
        frames.Frame();
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        if (sparse)
            frames.World.SetSparse(source, new SparseValue(7));
        else
            frames.World.Set(source, new Value(7));

        frames.Frame();
        frames.Frame();

        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));
    }

    [Fact]
    public void RepeatedReadsKeepEachEntityAndComponentInTheDisplayedValues()
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.Create(new Value(3));
        frames.World.SetSparse(first, new SparseValue(5));
        Entity second = frames.World.Create(new Value(7));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CombinedDisplay(first, second));
        frames.Frame();
        Assert.Equal("3+3+5+7", frames.World.ReadComponentData<DisplayText>(root).Value);

        frames.World.Set(first, new Value(11));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7", frames.World.ReadComponentData<DisplayText>(root).Value);
        frames.World.SetSparse(first, new SparseValue(13));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7;11+11+13+7", frames.World.ReadComponentData<DisplayText>(root).Value);
        frames.World.Set(second, new Value(17));
        frames.Frame();
        Assert.Equal("3+3+5+7;11+11+5+7;11+11+13+7;11+11+13+17", frames.World.ReadComponentData<DisplayText>(root).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));
    }

    [Fact]
    public void NestedBuildsKeepTheirReadsWithTheirOwnDisplays()
    {
        using var frames = new HeadlessFrameDriver();
        Entity parentSource = frames.World.Create(new Value(3));
        Entity childSource = frames.World.Create(new Value(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SeparateGroup(parentSource, childSource));
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        ulong parentDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(root);

        frames.World.Set(childSource, new Value(7));
        frames.Frame();
        Assert.Equal("5;7", frames.World.ReadComponentData<DisplayText>(child).Value);
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(parentDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(root));

        ulong childDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(child);
        frames.World.Set(parentSource, new Value(11));
        frames.Frame();
        Assert.Equal("3;11", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(childDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(child));

        frames.World.Set(parentSource, new Value(13));
        frames.World.Set(childSource, new Value(17));
        frames.Frame();
        Assert.Equal("3;11;13", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal("5;7;17", frames.World.ReadComponentData<DisplayText>(child).Value);
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

    private readonly record struct RewritingGroup(Entity Trigger, Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Scope.Read<Value>(Trigger).Number;
            Scope.View(new PairDisplay(Trigger, Source, Sparse, value * 10));
            if (value == 7)
            {
                if (Sparse)
                    Scope.World.SetSparse(Source, new SparseValue(11));
                else
                    Scope.World.Set(Source, new OtherValue(11));
            }
            return default;
        }
    }

    private readonly record struct InitialWriteGroup(Entity Source, bool Sparse) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.View(new ValueDisplay(Source, Sparse));
            if (Sparse)
                Scope.World.SetSparse(Source, new SparseValue(7));
            else
                Scope.World.Set(Source, new Value(7));
            return default;
        }
    }

    private readonly record struct PairDisplay(Entity Trigger, Entity Source, bool Sparse, int Parameter) : IView, IComponent
    {
        public BuildTask Build()
        {
            int trigger = Scope.Read<Value>(Trigger).Number;
            int value = Sparse ? Scope.Read<SparseValue>(Source).Number : Scope.Read<OtherValue>(Source).Number;
            AppendDisplay($"{Parameter}:{trigger}+{value}");
            return default;
        }
    }

#if DEBUG
    [Fact]
    public void ReadOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.Read<Value>(Entity.Null));
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
            WriteDisplay(Scope.World.ReadComponentData<DisplayText>(child).Value);
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
            text = $"{Scope.World.ReadComponentData<DisplayText>(Scope.Entity).Value};{text}";
        WriteDisplay(text);
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Set(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct Value(int Number) : ITrackedComponent;
    private readonly record struct OtherValue(int Number) : ITrackedComponent;
    private readonly record struct Visibility(bool Shown) : ITrackedComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent, ITrackedComponent;
    private readonly record struct DisplayText(string Value) : ITrackedComponent;
}
