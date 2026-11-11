using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ComponentWriteTests
{
    [Fact]
    public void SharedComponentWriteChecksPass() => ComponentWriteChecks.Verify();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualWritesPreserveTheComponentValueAndVersion(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseNumber(3));
        else
            frames.World.Add(source, new Number(3));
        ulong version = sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source);
        ulong typeVersion = sparse
            ? frames.World.ComponentWriteVersion(ComponentId<SparseNumber>.Value)
            : frames.World.ComponentWriteVersion(ComponentId<Number>.Value);
        Views.Spawn(frames.World, Entity.Null, new NumberWriter(source, sparse, 3));

        frames.Frame();

        Assert.Equal(3, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(version, sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source));
        Assert.Equal(typeVersion, sparse
            ? frames.World.ComponentWriteVersion(ComponentId<SparseNumber>.Value)
            : frames.World.ComponentWriteVersion(ComponentId<Number>.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ChangedWritesCommitTheirValuesWhenTheBuildCompletes(bool sparse, bool exists)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (exists)
        {
            if (sparse)
                frames.World.SetSparse(source, new SparseNumber(3));
            else
                frames.World.Add(source, new Number(3));
        }
        ulong version = !exists ? 0 : sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source);
        Entity root = Views.Spawn(frames.World, Entity.Null, new DeferredNumberWriter(source, sparse));

        frames.Frame();

        Assert.Equal(exists ? 3 : 0, frames.World.ReadComponentData<ObservedNumber>(root).CopyValue);
        Assert.Equal(exists ? 3 : 0, frames.World.ReadComponentData<ObservedNumber>(root).StoredValue);
        Assert.Equal(version, frames.World.ReadComponentData<ObservedNumber>(root).Version);
        Assert.Equal(7, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        Assert.NotEqual(version, sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EnsureSuppliesDefaultDataAndPreservesExistingValues(bool sparse, bool exists)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (exists)
        {
            if (sparse)
                frames.World.SetSparse(source, new SparseNumber(11));
            else
                frames.World.Add(source, new Number(11));
        }
        ulong version = !exists ? 0 : sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source);
        Views.Spawn(frames.World, Entity.Null, new NumberRequirement(source, sparse));

        frames.Frame();

        Assert.Equal(exists ? 11 : 0, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        ulong addedVersion = sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source);
        if (exists)
            Assert.Equal(version, addedVersion);
        else
            Assert.NotEqual(0u, addedVersion);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadWriteSuppliesDefaultDataAndTracksLaterChanges(bool sparse, bool exists)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (exists)
        {
            if (sparse)
                frames.World.SetSparse(source, new SparseNumber(11));
            else
                frames.World.Add(source, new Number(11));
        }
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberDisplay(source, sparse));

        frames.Frame();

        Assert.Equal(exists ? 11 : 0, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
        Assert.Equal(exists ? 11 : 0, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        if (sparse)
            frames.World.SetSparse(source, new SparseNumber(17));
        else
            frames.World.Set(source, new Number(17));

        frames.Frame();

        Assert.Equal(17, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CounterButtonDisplaysTheLatestCountWithinTheFrame(bool sparse, bool readWrite)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseNumber(0));
        else
            frames.World.Add(source, new Number(0));
        Entity clicks = frames.World.Create(new ClickCount(4));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CounterButton(source, clicks, sparse, readWrite));

        frames.Frame();

        Assert.Equal(4, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
        Assert.Equal(4, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        frames.World.Set(clicks, new ClickCount(7));

        frames.Frame();

        Assert.Equal(7, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayedNumber>(root);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayedNumber>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteKeepsExternalChangesUntilItsParametersChange(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberWriter(source, sparse, 3));
        frames.Frame();
        if (sparse)
            frames.World.SetSparse(source, new SparseNumber(7));
        else
            frames.World.Set(source, new Number(7));

        frames.Frame();

        Assert.Equal(7, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        frames.World.Set(root, new NumberWriter(source, sparse, 11));
        frames.Frame();
        Assert.Equal(11, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
    }

    [Fact]
    public void WriteReferencesSurviveStructureChangesAndNestedBuilds()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new Number(3));
        frames.World.SetSparse(source, new SparseNumber(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReferenceGroup(source));

        frames.Frame();

        Assert.Equal(43, frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(49, frames.World.ReadComponentData<SparseNumber>(source).Value);
        Assert.Equal(43, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal(101, frames.World.ReadComponentData<Number>(child).Value);
        Assert.True(frames.World.Has<ExtraData>(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionComparesTheCopyWithTheValueCurrentlyStored(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseNumber(3));
        else
            frames.World.Add(source, new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CurrentNumberWriter(source, sparse));

        frames.Frame();

        Assert.Equal(7, sparse
            ? frames.World.ReadComponentData<SparseNumber>(source).Value
            : frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(frames.World.ReadComponentData<ObservedNumber>(root).Version, sparse
            ? frames.World.ComponentDataWriteVersion<SparseNumber>(source)
            : frames.World.ComponentDataWriteVersion<Number>(source));
    }

    [Fact]
    public void AFailedBuildPreservesStoredDataAndAllowsALaterSuccessfulBuild()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new Number(3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new FailingWriter(source, true));
        ulong version = frames.World.ComponentDataWriteVersion<Number>(source);

        frames.Frame();

        Assert.IsType<InvalidOperationException>(frames.World.ReadComponentData<BuildError>(root).Exception);
        Assert.Equal(3, frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(version, frames.World.ComponentDataWriteVersion<Number>(source));
        frames.World.Set(root, new FailingWriter(source, false));
        frames.Frame();
        Assert.Equal(7, frames.World.ReadComponentData<Number>(source).Value);
        Assert.False(frames.World.HasSparse<BuildError>(root));
    }

#if DEBUG
    [Fact]
    public void ComponentWritesOutsideBuildReportTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.Write<Number>(Entity.Null));
        Assert.Throws<InvalidOperationException>(() => Scope.ReadWrite<Number>(Entity.Null));
        Assert.Throws<InvalidOperationException>(() => Scope.Ensure<Number>(Entity.Null));
    }
#endif

    private readonly record struct FailingWriter(Entity Source, bool Throw) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<Number>(Source) = new Number(7);
            if (Throw)
                throw new InvalidOperationException("test build failure");
            return default;
        }
    }

    private readonly record struct CurrentNumberWriter(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            ulong version;
            if (Sparse)
            {
                Scope.Write<SparseNumber>(Source) = new SparseNumber(7);
                Scope.World.SetSparse(Source, new SparseNumber(7));
                version = Scope.World.ComponentDataWriteVersion<SparseNumber>(Source);
            }
            else
            {
                Scope.Write<Number>(Source) = new Number(7);
                Scope.World.Set(Source, new Number(7));
                version = Scope.World.ComponentDataWriteVersion<Number>(Source);
            }
            Scope.World.Add(Scope.Entity, new ObservedNumber(3, 7, version));
            return default;
        }
    }

    private readonly record struct ReferenceGroup(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref Number table = ref Scope.Write<Number>(Source);
            ref SparseNumber sparse = ref Scope.Write<SparseNumber>(Source);
            Scope.Ensure<ExtraData>(Source);
            for (int value = 0; value < 128; value++)
            {
                Entity entity = Scope.World.Create();
                Scope.World.SetSparse(entity, new SparseNumber(value));
                Scope.Write<Number>(entity) = new Number(value);
            }
            Scope.View(new NestedNumber(101));
            Scope.Write<Number>(Source) = new Number(41);
            Scope.Write<SparseNumber>(Source) = new SparseNumber(47);
            table = new Number(table.Value + 2);
            sparse = new SparseNumber(sparse.Value + 2);
            Scope.Write<DisplayedNumber>(Scope.Entity) = new DisplayedNumber(table.Value);
            return default;
        }
    }

    private readonly record struct NestedNumber(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<Number>(Scope.Entity) = new Number(Value);
            return default;
        }
    }

    private readonly record struct ExtraData : IComponent;

    private readonly record struct CounterButton(Entity Source, Entity Clicks, bool Sparse, bool ReadWrite) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Sparse
                ? (ReadWrite ? Scope.ReadWrite<SparseNumber>(Source).Value : Scope.Read<SparseNumber>(Source).Value)
                : (ReadWrite ? Scope.ReadWrite<Number>(Source).Value : Scope.Read<Number>(Source).Value);
            Scope.Write<DisplayedNumber>(Scope.Entity) = new DisplayedNumber(value);
            int count = Scope.Read<ClickCount>(Clicks).Value;
            if (value < count)
            {
                if (Sparse)
                    Scope.Write<SparseNumber>(Source) = new SparseNumber(count);
                else
                    Scope.Write<Number>(Source) = new Number(count);
            }
            return default;
        }
    }

    private readonly record struct ClickCount(int Value) : ITrackedComponent;

    private readonly record struct NumberDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Sparse
                ? Scope.ReadWrite<SparseNumber>(Source).Value
                : Scope.ReadWrite<Number>(Source).Value;
            Scope.Write<DisplayedNumber>(Scope.Entity) = new DisplayedNumber(value);
            return default;
        }
    }

    private readonly record struct DisplayedNumber(int Value) : IComponent;

    private readonly record struct NumberRequirement(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Sparse)
            {
                Scope.Ensure<SparseNumber>(Source);
                Scope.Ensure<SparseNumber>(Source);
            }
            else
            {
                Scope.Ensure<Number>(Source);
                Scope.Ensure<Number>(Source);
            }
            return default;
        }
    }

    private readonly record struct DeferredNumberWriter(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int copyValue;
            int storedValue;
            ulong version;
            if (Sparse)
            {
                ref SparseNumber number = ref Scope.Write<SparseNumber>(Source);
                copyValue = number.Value;
                number = new SparseNumber(7);
                bool exists = Scope.World.HasSparse<SparseNumber>(Source);
                storedValue = exists ? Scope.World.ReadComponentData<SparseNumber>(Source).Value : 0;
                version = exists ? Scope.World.ComponentDataWriteVersion<SparseNumber>(Source) : 0;
            }
            else
            {
                ref Number number = ref Scope.Write<Number>(Source);
                copyValue = number.Value;
                number = new Number(7);
                bool exists = Scope.World.Has<Number>(Source);
                storedValue = exists ? Scope.World.ReadComponentData<Number>(Source).Value : 0;
                version = exists ? Scope.World.ComponentDataWriteVersion<Number>(Source) : 0;
            }
            Scope.World.Add(Scope.Entity, new ObservedNumber(copyValue, storedValue, version));
            return default;
        }
    }

    private readonly record struct ObservedNumber(int CopyValue, int StoredValue, ulong Version) : IComponent;

    private readonly record struct NumberWriter(Entity Source, bool Sparse, int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Sparse)
                Scope.Write<SparseNumber>(Source) = new SparseNumber(Value);
            else
                Scope.Write<Number>(Source) = new Number(Value);
            return default;
        }
    }

    private readonly record struct Number(int Value) : ITrackedComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent, ITrackedComponent;
}
