using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class BuildConvergenceTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void MutualComponentWritesStopAtTheConfiguredLimitAndCanRecover(int iterationLimit)
    {
        using var frames = new HeadlessFrameDriver(iterationLimit);
        Entity enabled = frames.World.Create(new FeedbackEnabled(true));
        Entity source = frames.World.Create(new Number(0));
        frames.World.SetSparse(source, new SparseNumber(0));
        Views.Spawn(frames.World, Entity.Null, new TableFeedback(source, enabled));
        Views.Spawn(frames.World, Entity.Null, new SparseFeedback(source, enabled));

#if DEBUG
        Assert.Throws<InvalidOperationException>(frames.Frame);
#else
        frames.Frame();
#endif

        int table = frames.World.ReadComponentData<Number>(source).Value;
        int sparse = frames.World.ReadComponentData<SparseNumber>(source).Value;
        Assert.True(table > 0);
        Assert.True(sparse > 0);
        ulong tableVersion = frames.World.ComponentDataWriteVersion<Number>(source);
        ulong sparseVersion = frames.World.ComponentDataWriteVersion<SparseNumber>(source);
        frames.World.Set(enabled, new FeedbackEnabled(false));

        frames.Frame();
        frames.Frame();

        Assert.Equal(table, frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(sparse, frames.World.ReadComponentData<SparseNumber>(source).Value);
        Assert.Equal(tableVersion, frames.World.ComponentDataWriteVersion<Number>(source));
        Assert.Equal(sparseVersion, frames.World.ComponentDataWriteVersion<SparseNumber>(source));
    }

    [Fact]
    public void RebuildsCanConvergeOnTheLastAllowedRound()
    {
        using var frames = new HeadlessFrameDriver(iterationLimit: 2);
        Entity source = frames.World.Create(new Number(0));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConvergingDisplay(source));

        frames.Frame();

        Assert.Equal(1, frames.World.ReadComponentData<Number>(source).Value);
        Assert.Equal(1, frames.World.ReadComponentData<DisplayedNumber>(root).Value);
    }

    [Fact]
    public void PresenceAndMembershipObserveComponentAdditionsWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        Query query = frames.World.Query(new QueryDescription().With<Number>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new StructureGroup(source, query));

        frames.Frame();

        Entity observer = ElementChildren.Read(frames.World, root)[0];
        Assert.Equal(new StructureValues(true, 1), frames.World.ReadComponentData<StructureValues>(observer));
        Assert.Equal(7, frames.World.ReadComponentData<Number>(source).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<StructureValues>(observer);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<StructureValues>(observer));
    }

    private readonly record struct StructureGroup(Entity Source, Query Query) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new StructureDisplay(Source, Query));
            Scope.View(new NumberAddition(Source));
            return default;
        }
    }

    private readonly record struct StructureDisplay(Entity Source, Query Query) : IView, IComponent
    {
        public BuildTask Build()
        {
            bool present = Scope.Has<Number>(Source);
            int members = 0;
            foreach (Entity entity in Scope.Each(Query))
                members++;
            Scope.Write<StructureValues>(Scope.Entity) = new StructureValues(present, members);
            return default;
        }
    }

    private readonly record struct NumberAddition(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<Number>(Source) = new Number(7);
            return default;
        }
    }

    private readonly record struct StructureValues(bool Present, int Members) : IComponent;

    private readonly record struct ConvergingDisplay(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref Number number = ref Scope.ReadWrite<Number>(Source);
            Scope.Write<DisplayedNumber>(Scope.Entity) = new DisplayedNumber(number.Value);
            if (number.Value == 0)
                number = new Number(1);
            return default;
        }
    }

    private readonly record struct TableFeedback(Entity Source, Entity Enabled) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Scope.Read<FeedbackEnabled>(Enabled).Value)
                Scope.Write<Number>(Source) = new Number(Scope.Read<SparseNumber>(Source).Value + 1);
            return default;
        }
    }

    private readonly record struct SparseFeedback(Entity Source, Entity Enabled) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Scope.Read<FeedbackEnabled>(Enabled).Value)
                Scope.Write<SparseNumber>(Source) = new SparseNumber(Scope.Read<Number>(Source).Value + 1);
            return default;
        }
    }

    private readonly record struct Number(int Value) : ITrackedComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent, ITrackedComponent;
    private readonly record struct FeedbackEnabled(bool Value) : ITrackedComponent;
    private readonly record struct DisplayedNumber(int Value) : IComponent;
}
