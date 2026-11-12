using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public class TimelineTests
{
    [Fact]
    public void DurationStageKeepsTimelineEnabledWhileWaitingForItsBoundary()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline());
        world.AddBuffer<TimelineStage>(entity);
        {
            using var stages = world.GetBuffer<TimelineStage>(entity);
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.5f });

        }
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TimelineEndSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.25);

        systems.Update(ref frame);
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(0u, world.ReadComponentData<Timeline>(entity).Completions);
        systems.Update(ref frame);
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.ReadComponentData<Timeline>(entity).Completions);
    }

#if DEBUG
    [Fact]
    public void ExternalAdvanceAndUnknownBoundariesReportDevelopmentChecks()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline());
        world.AddBuffer<TimelineStage>(entity);
        {
            using var stages = world.GetBuffer<TimelineStage>(entity);
stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 1 });

        }
        Assert.Throws<InvalidOperationException>(() => Animation.Advance(world, entity));
        {
            using var stages = world.GetBuffer<TimelineStage>(entity);
stages[0].End = (StageEnd)255;

        }
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineEndSystem());
        Assert.Throws<InvalidOperationException>(() => systems.Update());
    }
#endif

    [Fact]
    public void DefaultTimelineAdvancesFromTheWorldFrameDeltaAtUnitSpeed()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.125);

        systems.Update(ref frame);

        Assert.Equal(0.125f, world.ReadComponentData<Timeline>(entity).Time);
    }

    [Fact]
    public void TimelineEventsCountRepeatedEmissionsIndependently()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline { Speed = 1 });
        world.AddBuffer<TimelineEvent>(entity);

        Animation.EmitEvent(world, entity, 7);
        Animation.EmitEvent(world, entity, 9);
        Animation.EmitEvent(world, entity, 7);

        {
            using var events = world.ReadBuffer<TimelineEvent>(entity);
            Assert.Equal(2, events.Count);
            Assert.Equal(7, events[0].Id);
            Assert.Equal(2u, events[0].Count);
            Assert.Equal(9, events[1].Id);
            Assert.Equal(1u, events[1].Count);

        }
    }

    [Fact]
    public void StagesAdvanceAtDurationSettlementAndExternalBoundaries()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline { Speed = 1 });
        world.AddBuffer<TimelineStage>(entity);
        {
            using var stages = world.GetBuffer<TimelineStage>(entity);
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.2f });
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
            stages.Add(new TimelineStage { End = StageEnd.Settled });
            stages.Add(new TimelineStage { End = StageEnd.External });

        }
        Entity activity = world.Create(new Activity { Running = true });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new ActivitySystem(activity));
        systems.Add(new TimelineEndSystem());
        var frame = new ImmediateSystemContext(world, 0, 0, 0.35);

        systems.Update(ref frame);
        Assert.Equal(2, world.ReadComponentData<Timeline>(entity).Stage);
        Assert.InRange(world.ReadComponentData<Timeline>(entity).Time, 0.04999f, 0.05001f);
        systems.Update(ref frame);
        Assert.Equal(2, world.ReadComponentData<Timeline>(entity).Stage);

        world.Set(activity, new Activity { Running = false });
        systems.Update(ref frame);
        Assert.Equal(3, world.ReadComponentData<Timeline>(entity).Stage);
        Assert.Equal(0, world.ReadComponentData<Timeline>(entity).Time);
        systems.Update(ref frame);
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(3, world.ReadComponentData<Timeline>(entity).Stage);

        Animation.Advance(world, entity);
        Assert.Equal(4, world.ReadComponentData<Timeline>(entity).Stage);
        Assert.Equal(0, world.ReadComponentData<Timeline>(entity).Time);
        systems.Update(ref frame);
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.ReadComponentData<Timeline>(entity).Completions);
    }

    [Fact]
    public void RunningBitsKeepTimelineEnabledAndCompletionPreservesTableMembership()
    {
        using var world = new World();
        Entity entity = world.Create(new Timeline { Speed = 1 });
        Entity activity = world.Create(new Activity { Running = true });
        var timelines = world.Query(new QueryDescription().WithStored<Timeline>());
        ulong members = timelines.MembershipVersion;
        using var tables = world.WatchMembership(timelines);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new ActivitySystem(activity));
        systems.Add(new TimelineEndSystem());

        systems.Update();
        Assert.True(world.IsEnabled<Timeline>(entity));
        Assert.Equal(0u, world.ReadComponentData<Timeline>(entity).Completions);

        world.Set(activity, new Activity { Running = false });
        systems.Update();
        systems.Update();
        Assert.False(world.IsEnabled<Timeline>(entity));
        Assert.Equal(1u, world.ReadComponentData<Timeline>(entity).Completions);

        world.Enable<Timeline>(entity);
        systems.Update();
        Assert.Equal(2u, world.ReadComponentData<Timeline>(entity).Completions);
        Assert.Equal(members, timelines.MembershipVersion);
        using var membership = tables.Open();
        Assert.Equal(0, membership.Count);
        Assert.True(world.Has<Timeline>(entity));

    }

    [Fact]
    public void InterfaceClockAdvancesWhileGameAndUnscaledClocksArePaused()
    {
        using var world = new World();
        Entity time = world.Create(new AnimationTime(0.25f, gameScale: 0.5f, gamePaused: true));
        Entity game = world.Create(new Timeline { Clock = TimelineClock.Game, Speed = 2 });
        Entity screen = world.Create(new Timeline { Clock = TimelineClock.Interface, Speed = 2 });
        Entity unscaled = world.Create(new Timeline { Clock = TimelineClock.Unscaled, Speed = 2 });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem(time));

        systems.Update();

        Assert.Equal(0, world.ReadComponentData<Timeline>(game).Time);
        Assert.Equal(0.5f, world.ReadComponentData<Timeline>(screen).Time);
        Assert.Equal(0, world.ReadComponentData<Timeline>(unscaled).Time);

        world.Set(time, new AnimationTime(0.25f, gameScale: 0.5f));
        systems.Update();

        Assert.Equal(0.25f, world.ReadComponentData<Timeline>(game).Time);
        Assert.Equal(1, world.ReadComponentData<Timeline>(screen).Time);
        Assert.Equal(0.5f, world.ReadComponentData<Timeline>(unscaled).Time);
    }

    private struct Activity : IComponent
    {
        public bool Running;
    }

    private struct ActivitySystem(Entity activity) : ISystem<ImmediateSystemContext>
    {
        private readonly Entity _activity = activity;
        private Query _timelines;

        public void OnCreate(ref ImmediateSystemContext context) =>
            _timelines = context.World.Query(new QueryDescription().WithStored<Timeline>().With<Timeline>());

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            bool running = context.World.ReadComponentData<Activity>(_activity).Running;
            using QueryScope rows = _timelines.Open();
            foreach (QueryChunk chunk in rows)
            {
                Span<Timeline> timelines = chunk.Write<Timeline>();
                if (running)
                    foreach (ref Timeline timeline in timelines) timeline.Submit(running);
            }
        }

        public void OnDestroy(ref ImmediateSystemContext context) { }
    }
}
