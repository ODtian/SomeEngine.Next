using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views.Tests;

public sealed partial class BuildConvergenceTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void MutualComponentWritesStopAtTheConfiguredLimitAndCanRecover(int iterationLimit)
    {
        using var frames = new HeadlessFrameDriver(iterationLimit);
        Entity enabled = frames.World.CreateEntity(new FeedbackEnabled(true));
        Entity source = frames.World.CreateEntity(new Number(0));
        frames.World.AddSparse(source, new SparseNumber(0));
        Views.Spawn(frames.World, Entity.Null, new TableFeedback(source, enabled));
        Views.Spawn(frames.World, Entity.Null, new SparseFeedback(source, enabled));

#if DEBUG
        Assert.Throws<InvalidOperationException>(frames.Frame);
#else
        frames.Frame();
#endif

        int table = frames.World.Read<Number>(source).Value;
        int sparse = frames.World.ReadSparse<SparseNumber>(source).Value;
        Assert.True(table > 0);
        Assert.True(sparse > 0);
        uint tableVersion = frames.World.WriteVersion<Number>(source);
        uint sparseVersion = frames.World.SparseWriteVersion<SparseNumber>(source);
        frames.World.Replace(enabled, new FeedbackEnabled(false));

        frames.Frame();
        frames.Frame();

        Assert.Equal(table, frames.World.Read<Number>(source).Value);
        Assert.Equal(sparse, frames.World.ReadSparse<SparseNumber>(source).Value);
        Assert.Equal(tableVersion, frames.World.WriteVersion<Number>(source));
        Assert.Equal(sparseVersion, frames.World.SparseWriteVersion<SparseNumber>(source));
    }

    [Fact]
    public void RebuildsCanConvergeOnTheLastAllowedRound()
    {
        using var frames = new HeadlessFrameDriver(iterationLimit: 2);
        Entity source = frames.World.CreateEntity(new Number(0));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ConvergingDisplay(source));

        frames.Frame();

        Assert.Equal(1, frames.World.Read<Number>(source).Value);
        Assert.Equal(1, frames.World.Read<DisplayedNumber>(root).Value);
    }

    [Fact]
    public void PresenceAndMembershipObserveComponentAdditionsWithinTheFrame()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Number>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new StructureGroup(source, query));

        frames.Frame();

        Entity observer = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Assert.Equal(new StructureValues(true, 1), frames.World.Read<StructureValues>(observer));
        Assert.Equal(7, frames.World.Read<Number>(source).Value);
        uint displayVersion = frames.World.WriteVersion<StructureValues>(observer);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<StructureValues>(observer));
    }

    private readonly record struct StructureGroup(Entity Source, QueryHandle Query) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new StructureDisplay(Source, Query));
            Scope.View(new NumberAddition(Source));
            return default;
        }
    }

    private readonly record struct StructureDisplay(Entity Source, QueryHandle Query) : IView, IComponent
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

    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent;
    private readonly record struct FeedbackEnabled(bool Value) : IComponent;
    private readonly record struct DisplayedNumber(int Value) : IComponent;
}
