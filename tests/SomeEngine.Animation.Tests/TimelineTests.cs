using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public class TimelineTests
{
    [Fact]
    public void DurationStageKeepsTimelineEnabledWhileWaitingForItsBoundary()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline());
        world.AddBuffer<TimelineStage>(entity);
        world.ExecuteBufferWrite<TimelineStage>(entity, static stages =>
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.5f }));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TimelineEndSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.25);

        systems.Update(ref frame);
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(0u, world.Read<Timeline>(entity).Completions);
        systems.Update(ref frame);
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.Read<Timeline>(entity).Completions);
    }

#if DEBUG
    [Fact]
    public void ExternalAdvanceAndUnknownBoundariesReportDevelopmentChecks()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline());
        world.AddBuffer<TimelineStage>(entity);
        world.ExecuteBufferWrite<TimelineStage>(entity, static stages =>
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 1 }));
        Assert.Throws<InvalidOperationException>(() => Animation.Advance(world, entity));
        world.ExecuteBufferWrite<TimelineStage>(entity, static stages => stages[0].End = (StageEnd)255);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineEndSystem());
        Assert.Throws<InvalidOperationException>(() => systems.Update());
    }
#endif

    [Fact]
    public void DefaultTimelineAdvancesFromTheWorldFrameDeltaAtUnitSpeed()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.125);

        systems.Update(ref frame);

        Assert.Equal(0.125f, world.Read<Timeline>(entity).Time);
    }

    [Fact]
    public void TimelineEventsCountRepeatedEmissionsIndependently()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline { Speed = 1 });
        world.AddBuffer<TimelineEvent>(entity);

        Animation.EmitEvent(world, entity, 7);
        Animation.EmitEvent(world, entity, 9);
        Animation.EmitEvent(world, entity, 7);

        world.ExecuteBufferRead<TimelineEvent>(entity, static events =>
        {
            Assert.Equal(2, events.Count);
            Assert.Equal(7, events[0].Id);
            Assert.Equal(2u, events[0].Count);
            Assert.Equal(9, events[1].Id);
            Assert.Equal(1u, events[1].Count);
        });
    }

    [Fact]
    public void StagesAdvanceAtDurationSettlementAndExternalBoundaries()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline { Speed = 1 });
        world.AddBuffer<TimelineStage>(entity);
        world.ExecuteBufferWrite<TimelineStage>(entity, static stages =>
        {
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.2f });
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
            stages.Add(new TimelineStage { End = StageEnd.Settled });
            stages.Add(new TimelineStage { End = StageEnd.External });
        });
        Entity activity = world.CreateEntity(new Activity { Running = true });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new ActivitySystem(activity));
        systems.Add(new TimelineEndSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.35);

        systems.Update(ref frame);
        Assert.Equal(2, world.Read<Timeline>(entity).Stage);
        Assert.InRange(world.Read<Timeline>(entity).Time, 0.04999f, 0.05001f);
        systems.Update(ref frame);
        Assert.Equal(2, world.Read<Timeline>(entity).Stage);

        world.Replace(activity, new Activity { Running = false });
        systems.Update(ref frame);
        Assert.Equal(3, world.Read<Timeline>(entity).Stage);
        Assert.Equal(0, world.Read<Timeline>(entity).Time);
        systems.Update(ref frame);
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(3, world.Read<Timeline>(entity).Stage);

        Animation.Advance(world, entity);
        Assert.Equal(4, world.Read<Timeline>(entity).Stage);
        Assert.Equal(0, world.Read<Timeline>(entity).Time);
        systems.Update(ref frame);
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.Read<Timeline>(entity).Completions);
    }

    [Fact]
    public void RunningBitsKeepTimelineEnabledAndCompletionPreservesTableMembership()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Timeline { Speed = 1 });
        Entity activity = world.CreateEntity(new Activity { Running = true });
        using var tables = world.ObserveEntityTables();
        tables.Accept(tables.Read());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new ActivitySystem(activity));
        systems.Add(new TimelineEndSystem());

        systems.Update();
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(0u, world.Read<Timeline>(entity).Completions);

        world.Replace(activity, new Activity { Running = false });
        systems.Update();
        systems.Update();
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.Read<Timeline>(entity).Completions);

        world.Enable<Timeline>(entity);
        systems.Update();
        Assert.Equal(2u, world.Read<Timeline>(entity).Completions);
        Assert.Equal(entity, Assert.Single(tables.Read().Entities));
        Assert.True(world.Has<Timeline>(entity));
    }

    [Fact]
    public void InterfaceClockAdvancesWhileGameAndUnscaledClocksArePaused()
    {
        using var world = new World();
        Entity time = world.CreateEntity(new AnimationTime(0.25f, gameScale: 0.5f, gamePaused: true));
        Entity game = world.CreateEntity(new Timeline { Clock = TimelineClock.Game, Speed = 2 });
        Entity screen = world.CreateEntity(new Timeline { Clock = TimelineClock.Interface, Speed = 2 });
        Entity unscaled = world.CreateEntity(new Timeline { Clock = TimelineClock.Unscaled, Speed = 2 });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem(time));

        systems.Update();

        Assert.Equal(0, world.Read<Timeline>(game).Time);
        Assert.Equal(0.5f, world.Read<Timeline>(screen).Time);
        Assert.Equal(0, world.Read<Timeline>(unscaled).Time);

        world.Replace(time, new AnimationTime(0.25f, gameScale: 0.5f));
        systems.Update();

        Assert.Equal(0.25f, world.Read<Timeline>(game).Time);
        Assert.Equal(1, world.Read<Timeline>(screen).Time);
        Assert.Equal(0.5f, world.Read<Timeline>(unscaled).Time);
    }

    private struct Activity : IComponent
    {
        public bool Running;
    }

    private struct ActivitySystem(Entity activity) : ISystem<ImmediateSystemContext>
    {
        private readonly Entity _activity = activity;
        private QueryHandle _timelines;

        public void OnCreate(ref ImmediateSystemContext context) =>
            _timelines = context.World.Query(context.World.QueryDefinition().ReadWrite<Timeline>().Enabled<Timeline>());

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            bool running = context.World.Read<Activity>(_activity).Running;
            context.World.ExecuteQuery(_timelines, ref running,
                static (QueryCursor rows, ref bool running) =>
                {
                    foreach (QueryRow row in rows.Rows)
                        if (running)
                            row.ReadWrite<Timeline>().Submit(running);
                });
        }

        public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_timelines);
    }
}
