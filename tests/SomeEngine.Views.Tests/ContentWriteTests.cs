using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ContentWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentSharesTheCallingCalculationsCopyUntilTheBuildCompletes(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseNumber(3));
        else
            frames.World.Add(source, new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentNumberGroup(source, sparse));

        frames.Frame();

        Assert.Equal(11, sparse
            ? frames.World.ReadSparse<SparseNumber>(source).Value
            : frames.World.Read<Number>(source).Value);
        Assert.Equal(new CopyValues(3, 9), frames.World.Read<CopyValues>(root));
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity nested = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, group));
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, nested));
        Assert.Equal(9, frames.World.Read<NumberLabel>(label).Value);
    }

    [Fact]
    public void CallerBuildCommitsItsOwnDataAndRetainsTheDeclaredChildren()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CallerNumberGroup(source));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Assert.Equal(3, frames.World.Read<Number>(container).Value);
        frames.World.Replace(source, new Number(7));

        frames.Frame();

        Assert.Equal(7, frames.World.Read<Number>(container).Value);
        Assert.Equal(label, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container)));
        Assert.Equal(31, frames.World.Read<NumberLabel>(label).Value);
    }

    [Fact]
    public void UntrackedReadWriteInsideContentPreservesTheFirstDisplayAfterSourceChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new UntrackedContentNumber(source));
        frames.Frame();
        Assert.Equal(7, frames.World.Read<Number>(source).Value);
        Assert.Equal(new CopyValues(3, 3), frames.World.Read<CopyValues>(root));
        frames.World.Replace(source, new Number(11));

        frames.Frame();

        Assert.Equal(11, frames.World.Read<Number>(source).Value);
        Assert.Equal(new CopyValues(3, 3), frames.World.Read<CopyValues>(root));
    }

    [Fact]
    public void FailedContentPreservesStoredDataAndAllowsALaterSuccessfulBuild()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new FailingContentNumber(source, true));
        uint version = frames.World.WriteVersion<Number>(source);

        frames.Frame();

        Assert.Equal("content write failed", frames.World.ReadSparse<BuildError>(root).Exception.Message);
        Assert.Equal(3, frames.World.Read<Number>(source).Value);
        Assert.Equal(version, frames.World.WriteVersion<Number>(source));
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(root, new FailingContentNumber(source, false));

        frames.Frame();

        Assert.Equal(7, frames.World.Read<Number>(source).Value);
        Assert.False(frames.World.HasSparse<BuildError>(root));
        Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
    }

    private readonly record struct FailingContentNumber(Entity Source, bool Throw) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
                Scope.Write<Number>(Source) = new Number(7);
            if (Throw)
                throw new InvalidOperationException("content write failed");
            return default;
        }
    }

    private readonly record struct UntrackedContentNumber(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            using (Scope.Untrack())
            {
                ref Number number = ref Scope.ReadWrite<Number>(Source);
                Scope.Write<CopyValues>(Scope.Entity) = new CopyValues(number.Value, number.Value);
                number = new Number(7);
            }
            return default;
        }
    }

    private readonly record struct ContentNumberGroup(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Sparse)
            {
                ref SparseNumber number = ref Scope.Write<SparseNumber>(Source);
                number = new SparseNumber(7);
                DeclareContent();
                Scope.Write<CopyValues>(Scope.Entity) = new CopyValues(
                    Scope.World.ReadSparse<SparseNumber>(Source).Value, number.Value);
                number = new SparseNumber(number.Value + 2);
            }
            else
            {
                ref Number number = ref Scope.Write<Number>(Source);
                number = new Number(7);
                DeclareContent();
                Scope.Write<CopyValues>(Scope.Entity) = new CopyValues(
                    Scope.World.Read<Number>(Source).Value, number.Value);
                number = new Number(number.Value + 2);
            }
            return default;
        }

        private void DeclareContent()
        {
            using (Scope.View(new Group()).Content())
            {
                IncreaseCopy();
                using (Scope.View(new Group()).Content())
                    Scope.View(new NumberLabel(IncreaseCopy()));
            }
        }

        private int IncreaseCopy()
        {
            if (Sparse)
            {
                ref SparseNumber number = ref Scope.Write<SparseNumber>(Source);
                number = new SparseNumber(number.Value + 1);
                return number.Value;
            }
            else
            {
                ref Number number = ref Scope.Write<Number>(Source);
                number = new Number(number.Value + 1);
                return number.Value;
            }
        }
    }

    private readonly record struct CallerNumberGroup(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new CallerNumber(Source)).Content())
                Scope.View(new NumberLabel(31));
            return default;
        }
    }

    private readonly record struct CallerNumber(Entity Source) : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            Scope.Write<Number>(Scope.Entity) = Scope.Read<Number>(Source);
            return default;
        }
    }

    private readonly record struct NumberLabel(int Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct CopyValues(int StoredValue, int CopyValue) : IComponent;
    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent;
}
