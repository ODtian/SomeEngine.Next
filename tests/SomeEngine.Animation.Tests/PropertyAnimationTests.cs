using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation.Tests;

public sealed class PropertyAnimationTests
{
    private static readonly Channel<float> Opacity = Channel<float>.Of<Visual>(
        static (ref Visual visual) => ref visual.Opacity);
    private static readonly Channel<Vector2> Position = Channel<Vector2>.Of<Visual>(
        static (ref Visual visual) => ref visual.Position);

    [Fact]
    public void TweenWritesTheSelectedFieldAndAdvancesItsVersionEveryFrame()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Number = 7, Opacity = 0.25f });
        PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Opacity, 0.75f, Spec.Tween(1));
        using var systems = FloatSystems(world);
        uint before = world.WriteVersion<Visual>(target);

        Tick(systems, world, 0.5);

        Assert.Equal(new Visual { Number = 7, Opacity = 0.5f }, world.Read<Visual>(target));
        uint middle = world.WriteVersion<Visual>(target);
        Assert.NotEqual(before, middle);
        Assert.True(world.IsEnabled<Timeline>(animation));

        Tick(systems, world, 0.5);

        Assert.Equal(0.75f, world.Read<Visual>(target).Opacity);
        Assert.NotEqual(middle, world.WriteVersion<Visual>(target));
        Assert.False(world.IsEnabled<Timeline>(animation));
        Assert.Equal(1u, world.Read<Timeline>(animation).Completions);
    }

    [Fact]
    public void SpringAdvancesTheFieldToItsTargetAndVersionsEachSample()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Spring());
        using var systems = FloatSystems(world);
        uint before = world.WriteVersion<Visual>(target);

        Tick(systems, world, 1d / 60);

        Assert.Equal(0.035260417f, world.Read<Visual>(target).Opacity, 6);
        Assert.NotEqual(before, world.WriteVersion<Visual>(target));
        for (int frame = 0; frame < 1200 && world.IsEnabled<Timeline>(animation); frame++)
        {
            uint version = world.WriteVersion<Visual>(target);
            Tick(systems, world, 1d / 60);
            Assert.NotEqual(version, world.WriteVersion<Visual>(target));
        }
        Assert.Equal(1, world.Read<Visual>(target).Opacity);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void RepeatedToRetargetsFromThePresentedValue()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        using var systems = FloatSystems(world);
        Tick(systems, world, 0.25);
        Assert.Equal(0.25f, world.Read<Visual>(target).Opacity);

        Entity retargeted = PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1));

        Assert.Equal(animation, retargeted);
        Assert.Equal(0.25f, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(1.125f, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(2, world.Read<Visual>(target).Opacity);
        Assert.Equal(1u, world.Read<Timeline>(animation).Completions);
    }

    [Fact]
    public void SpringToRetargetKeepsMomentumAndThePresentedValue()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Spring());
        using var systems = FloatSystems(world);
        Tick(systems, world, 1d / 60);
        float presented = world.Read<Visual>(target).Opacity;

        Entity retargeted = PropertyAnimation.To(world, target, Opacity, 2f, Spec.Spring());

        Assert.Equal(animation, retargeted);
        Assert.Equal(presented, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 1d / 60);
        // The reference spring keeps the first sample's velocity, then takes two 1/120 steps.
        Assert.Equal(0.13907372f, world.Read<Visual>(target).Opacity, 6);
    }

    [Fact]
    public void OrdinaryTargetsUsePooledEntitiesThatAreReusedAfterCompletion()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Visual());
        Entity second = world.CreateEntity(new Visual());
        Entity firstAnimation = PropertyAnimation.To(world, first, Opacity, 1f, Spec.Tween(1));
        Entity secondAnimation = PropertyAnimation.To(world, second, Opacity, 2f, Spec.Tween(1));
        Assert.NotEqual(first, firstAnimation);
        Assert.NotEqual(second, secondAnimation);
        Assert.NotEqual(firstAnimation, secondAnimation);
        using var systems = FloatSystems(world);

        Tick(systems, world, 1);

        Assert.Equal(1, world.Read<Visual>(first).Opacity);
        Assert.Equal(2, world.Read<Visual>(second).Opacity);
        Assert.True(world.IsAlive(firstAnimation));
        Assert.True(world.IsAlive(secondAnimation));
        Assert.False(world.IsEnabled<Timeline>(firstAnimation));
        Assert.False(world.IsEnabled<Timeline>(secondAnimation));
        int entityCount = world.EntityCount;

        Entity reusedFirst = PropertyAnimation.To(world, second, Opacity, 3f, Spec.Tween(1));
        Entity reusedSecond = PropertyAnimation.To(world, first, Opacity, 4f, Spec.Tween(1));

        Assert.Contains(reusedFirst, new[] { firstAnimation, secondAnimation });
        Assert.Contains(reusedSecond, new[] { firstAnimation, secondAnimation });
        Assert.NotEqual(reusedFirst, reusedSecond);
        Assert.Equal(entityCount, world.EntityCount);
        Tick(systems, world, 1);
        Assert.Equal(4, world.Read<Visual>(first).Opacity);
        Assert.Equal(3, world.Read<Visual>(second).Opacity);
    }

    [Fact]
    public void DeclaredTargetCanStartRetargetAndStopOneHundredTimesWithTheSameTableAndEntityCount()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.Declare<float>(world, target);
        using var tables = world.ObserveEntityTables();
        tables.Accept(tables.Read());
        int entityCount = world.EntityCount;
        using var systems = FloatSystems(world);

        for (int repetition = 0; repetition < 100; repetition++)
        {
            Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
            Assert.Equal(target, animation);
            Tick(systems, world, 0.25);
            float before = world.Read<Visual>(target).Opacity;
            PropertyAnimation.Retarget(world, animation, Opacity, 2f);
            Assert.Equal(before, world.Read<Visual>(target).Opacity);
            Tick(systems, world, 0.5);
            Assert.Equal((before + 2) / 2, world.Read<Visual>(target).Opacity, 5);
            PropertyAnimation.Stop(world, animation);
            float stopped = world.Read<Visual>(target).Opacity;
            Tick(systems, world, 1);
            Assert.Equal(stopped, world.Read<Visual>(target).Opacity);
            Assert.False(world.IsEnabled<Timeline>(animation));
        }

        Assert.Equal(entityCount, world.EntityCount);
        Assert.Equal(0, tables.Read().Count);
    }

    [Fact]
    public void PauseResumeAndSpeedPreserveProgressAndTableMembership()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(2));
        using var tables = world.ObserveEntityTables();
        tables.Accept(tables.Read());
        using var systems = FloatSystems(world);
        Tick(systems, world, 0.25);
        Assert.Equal(0.125f, world.Read<Visual>(target).Opacity);

        PropertyAnimation.SetSpeed(world, animation, 2);
        PropertyAnimation.Pause(world, animation);
        Tick(systems, world, 1);
        Assert.Equal(0.125f, world.Read<Visual>(target).Opacity);
        Assert.True(world.IsEnabled<Timeline>(animation));

        PropertyAnimation.Resume(world, animation);
        Tick(systems, world, 0.25);
        Assert.Equal(0.375f, world.Read<Visual>(target).Opacity);
        PropertyAnimation.SetSpeed(world, animation, 0);
        Tick(systems, world, 1);
        Assert.Equal(0.375f, world.Read<Visual>(target).Opacity);
        Assert.True(world.IsEnabled<Timeline>(animation));
        PropertyAnimation.SetSpeed(world, animation, 1);
        Tick(systems, world, 0.25);
        Assert.Equal(0.5f, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 1);
        Assert.Equal(1, world.Read<Visual>(target).Opacity);
        Assert.Equal(0, tables.Read().Count);
    }

    [Fact]
    public void BySamplesADisplacementThatReturnsToThePresentedValue()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Opacity = 10 });
        Entity animation = PropertyAnimation.By(world, target, Opacity, 4f, Spec.Tween(1));
        using var systems = FloatSystems(world);
        Assert.Equal(10, world.Read<Visual>(target).Opacity);

        Tick(systems, world, 0.5);

        Assert.Equal(12, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(10, world.Read<Visual>(target).Opacity);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void StoppingAnInlineTimelineThenStartingOneFieldKeepsTheOtherFieldsAtTheirStoppedValues()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Position = new Vector2(10, 20) });
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.Declare<Vector2>(world, target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        PropertyAnimation.To(world, target, Position, new Vector2(20, 40), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TrackSystem<Vector2, Vector2Ops>());
        systems.Add(new TimelineEndSystem());
        Tick(systems, world, 0.25);
        Assert.Equal(new Vector2(12.5f, 25), world.Read<Visual>(target).Position);

        PropertyAnimation.Stop(world, target);
        PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1));
        Tick(systems, world, 0.5);

        Assert.Equal(1.125f, world.Read<Visual>(target).Opacity);
        Assert.Equal(new Vector2(12.5f, 25), world.Read<Visual>(target).Position);
    }

    [Fact]
    public void ACompletedFieldCanBeChangedWhileAnotherFieldContinuesAnimating()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.Declare<Vector2>(world, target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(0.25f));
        PropertyAnimation.To(world, target, Position, new Vector2(4, 8), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TrackSystem<Vector2, Vector2Ops>());
        systems.Add(new TimelineEndSystem());
        Tick(systems, world, 0.25);
        Visual visual = world.Read<Visual>(target);
        Assert.Equal(1, visual.Opacity);
        visual.Opacity = 0.5f;
        world.Replace(target, visual);

        Tick(systems, world, 0.25);

        Assert.Equal(0.5f, world.Read<Visual>(target).Opacity);
        Assert.Equal(new Vector2(2, 4), world.Read<Visual>(target).Position);
        Assert.True(world.IsEnabled<Timeline>(target));
    }

    [Fact]
    public void DefaultSystemsAnimateBuiltInValuesWithTheSuppliedClock()
    {
        using var world = new World();
        Entity time = world.CreateEntity(new AnimationTime(0.25f));
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.Declare<Vector2>(world, target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        PropertyAnimation.To(world, target, Position, new Vector2(4, 8), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems, time);

        systems.Update();

        Assert.Equal(0.25f, world.Read<Visual>(target).Opacity);
        Assert.Equal(new Vector2(1, 2), world.Read<Visual>(target).Position);
        world.Replace(time, new AnimationTime(0.75f));
        systems.Update();
        Assert.Equal(1, world.Read<Visual>(target).Opacity);
        Assert.Equal(new Vector2(4, 8), world.Read<Visual>(target).Position);
        Assert.False(world.IsEnabled<Timeline>(target));
    }

    [Fact]
    public void CustomValuesUseToRetargetByAndSpringThroughTheirOwnOpsStruct()
    {
        using var world = new World();
        Channel<Meter> channel = Channel<Meter>.Of<Reading>(static (ref Reading reading) => ref reading.Value);
        Entity target = world.CreateEntity(new Reading { Value = new Meter(10) });
        PropertyAnimation.Declare<Meter>(world, target);
        Entity animation = PropertyAnimation.To(world, target, channel, new Meter(20), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<Meter, MeterOps>());
        systems.Add(new TimelineEndSystem());
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(15), world.Read<Reading>(target).Value);
        PropertyAnimation.Retarget(world, animation, channel, new Meter(30));
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(22.5f), world.Read<Reading>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(30), world.Read<Reading>(target).Value);

        PropertyAnimation.By(world, target, channel, new Meter(4), Spec.Tween(1));
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(32), world.Read<Reading>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(30), world.Read<Reading>(target).Value);
        PropertyAnimation.To(world, target, channel, new Meter(40), Spec.Spring(tolerance: 0.0001f));
        for (int frame = 0; frame < 1200 && world.IsEnabled<Timeline>(animation); frame++)
            Tick(systems, world, 1d / 60);
        Assert.Equal(new Meter(40), world.Read<Reading>(target).Value);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void SteadyAnimationSystemCallbacksAllocateZeroManagedBytes()
    {
        using var world = new World();
        Entity inline = world.CreateEntity(new Visual());
        Entity ordinary = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, inline);
        PropertyAnimation.To(world, inline, Opacity, 1f, Spec.Tween(10000));
        PropertyAnimation.To(world, ordinary, Opacity, 1f, Spec.Tween(10000));
        var clock = new TimelineSystem();
        var tracks = new TrackSystem<float, FloatOps>();
        var completion = new TimelineEndSystem();
        var context = new ImmediateSystemContext(world, 0, 0, 1d / 60);
        clock.OnCreate(ref context);
        tracks.OnCreate(ref context);
        completion.OnCreate(ref context);
        try
        {
            for (int frame = 0; frame < 512; frame++)
            {
                clock.OnUpdate(ref context);
                tracks.OnUpdate(ref context);
                completion.OnUpdate(ref context);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 128; frame++)
            {
                clock.OnUpdate(ref context);
                tracks.OnUpdate(ref context);
                completion.OnUpdate(ref context);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
            Assert.True(world.Read<Visual>(inline).Opacity > 0);
            Assert.Equal(world.Read<Visual>(inline).Opacity, world.Read<Visual>(ordinary).Opacity);
        }
        finally
        {
            completion.OnDestroy(ref context);
            tracks.OnDestroy(ref context);
            clock.OnDestroy(ref context);
        }
    }

    [Fact]
    public void KeyframeTracksSampleTheAnimationEntitysBuffer()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        world.AddBuffer<Keyframe>(target);
        world.ExecuteBufferWrite<Keyframe>(target, static keys =>
        {
            keys.Add(new Keyframe(0, 0));
            keys.Add(new Keyframe(0.5f, 0.8f, EasingType.QuadIn));
            keys.Add(new Keyframe(1, 1));
        });
        PropertyAnimation.To(world, target, Opacity, 10f, Spec.Keyframes(2));
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);
        Assert.Equal(4, world.Read<Visual>(target).Opacity, 5);
        Tick(systems, world, 1);
        Assert.Equal(8.5f, world.Read<Visual>(target).Opacity, 5);
        Tick(systems, world, 0.5);
        Assert.Equal(10, world.Read<Visual>(target).Opacity);
        Assert.False(world.IsEnabled<Timeline>(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterSourcesReplaceEarlierSourcesAndReportTheirSharedFieldInDevelopment(bool inline)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1), layer: 0);
        using var systems = FloatSystems(world);
        Tick(systems, world, 0.25);

#if DEBUG
        Assert.Throws<InvalidOperationException>(() =>
            PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1), layer: 1));
#else
        PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1), layer: 1);
#endif
        Tick(systems, world, 0.5);
        Assert.Equal(1.125f, world.Read<Visual>(target).Opacity);

#if DEBUG
        Assert.Throws<InvalidOperationException>(() =>
            PropertyAnimation.To(world, target, Opacity, 3f, Spec.Tween(1), layer: 0));
#else
        PropertyAnimation.To(world, target, Opacity, 3f, Spec.Tween(1), layer: 0);
#endif
        Tick(systems, world, 0.5);
        Assert.Equal(2.0625f, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 1);
        Assert.Equal(3, world.Read<Visual>(target).Opacity);
    }

    [Fact]
    public void OnePooledTimelineCanAnimateMultipleTargetsAndBeReusedAfterTheyFinish()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Visual());
        Entity second = world.CreateEntity(new Visual());
        Entity animation = PropertyAnimation.To(world, first, Opacity, 1f, Spec.Tween(1));
        var secondTrack = new Track<float>
        {
            Target = second, Channel = Opacity, To = 2, Duration = 1,
            Curve = Spec.Tween(1).Curve, StartId = world.Read<Timeline>(animation).Starts,
        };
        world.ExecuteBufferWrite<Track<float>, Track<float>>(animation, ref secondTrack,
            static (DynamicBuffer<Track<float>> tracks, ref Track<float> track) => tracks.Add(track));
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);
        Assert.Equal(0.5f, world.Read<Visual>(first).Opacity);
        Assert.Equal(1, world.Read<Visual>(second).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(1, world.Read<Visual>(first).Opacity);
        Assert.Equal(2, world.Read<Visual>(second).Opacity);
        Assert.False(world.IsEnabled<Timeline>(animation));
        Assert.Equal(animation, PropertyAnimation.To(world, first, Opacity, 3f, Spec.Tween(1)));
        Tick(systems, world, 1);
        Assert.Equal(3, world.Read<Visual>(first).Opacity);
        Assert.Equal(2, world.Read<Visual>(second).Opacity);
    }

    [Fact]
    public unsafe void AFunctionCurveAnimatesTheSelectedField()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.To(world, target, Opacity, 1f, new Spec(1, Curve.FromFunction(&Square)));
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);
        Assert.Equal(0.25f, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(1, world.Read<Visual>(target).Opacity);
    }

    private static float Square(float value) => value * value;

    [Fact]
    public void PublicTrackDataSamplesOnlyItsCurrentTimelineStage()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        world.AddBuffer<TimelineStage>(target);
        world.ExecuteBufferWrite<TimelineStage>(target, static stages =>
        {
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 1 });
            stages.Add(new TimelineStage { End = StageEnd.Settled });
        });
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        var next = new Track<float>
        {
            Channel = Opacity, From = 1, Current = 1, To = 3,
            Duration = 1, Curve = Spec.Tween(1).Curve, Stage = 1,
            StartId = world.Read<Timeline>(target).Starts,
        };
        world.ExecuteBufferWrite<Track<float>, Track<float>>(target, ref next,
            static (DynamicBuffer<Track<float>> tracks, ref Track<float> track) => tracks.Add(track));
        using var systems = FloatSystems(world);

        Tick(systems, world, 1);
        Assert.Equal(1, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(2, world.Read<Visual>(target).Opacity);
        Tick(systems, world, 0.5);
        Assert.Equal(3, world.Read<Visual>(target).Opacity);
        Assert.False(world.IsEnabled<Timeline>(target));
    }

    [Fact]
    public void RetargetKeepsTheCurrentSpringSourceAfterAnotherLayerWasReplaced()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Spring());
        using var systems = FloatSystems(world);
        Tick(systems, world, 1d / 60);
#if DEBUG
        Assert.Throws<InvalidOperationException>(() => PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1), layer: 1));
        Assert.Throws<InvalidOperationException>(() => PropertyAnimation.To(world, target, Opacity, 3f, Spec.Spring(), layer: 0));
#else
        PropertyAnimation.To(world, target, Opacity, 2f, Spec.Tween(1), layer: 1);
        PropertyAnimation.To(world, target, Opacity, 3f, Spec.Spring(), layer: 0);
#endif

        PropertyAnimation.Retarget(world, target, Opacity, 4f);
        Tick(systems, world, 1d / 60);

        Assert.Equal(0.17505881f, world.Read<Visual>(target).Opacity, 6);
    }

    [Fact]
    public void AReusedPoolEntityStartsWithFreshSpeedAndStageData()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Visual());
        Entity second = world.CreateEntity(new Visual());
        Entity animation = PropertyAnimation.To(world, first, Opacity, 1f, Spec.Tween(1));
        PropertyAnimation.SetSpeed(world, animation, 2);
        world.AddBuffer<TimelineStage>(animation);
        world.ExecuteBufferWrite<TimelineStage>(animation, static stages =>
            stages.Add(new TimelineStage { End = StageEnd.External }));
        PropertyAnimation.Pause(world, animation);
        PropertyAnimation.Stop(world, animation);

        Entity reused = PropertyAnimation.To(world, second, Opacity, 4f, Spec.Tween(1));
        Assert.Equal(animation, reused);
        using var systems = FloatSystems(world);
        Tick(systems, world, 0.5);

        Assert.Equal(2, world.Read<Visual>(second).Opacity);
        Assert.True(world.IsEnabled<Timeline>(reused));
        Tick(systems, world, 0.5);
        Assert.Equal(4, world.Read<Visual>(second).Opacity);
        Assert.False(world.IsEnabled<Timeline>(reused));
    }

    [Fact]
    public void DeclaringInlineStorageMovesTheNextToOntoTheTargetAndKeepsSpringMomentum()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        Entity pooled = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Spring());
        using var systems = FloatSystems(world);
        Tick(systems, world, 1d / 60);

        PropertyAnimation.Declare<float>(world, target);
        Entity inline = PropertyAnimation.To(world, target, Opacity, 2f, Spec.Spring());

        Assert.Equal(target, inline);
        Tick(systems, world, 1d / 60);
        Assert.Equal(0.13907372f, world.Read<Visual>(target).Opacity, 6);
        Assert.False(world.IsEnabled<Timeline>(pooled));
    }

    [Fact]
    public void PausingBeforeTheFirstDisplacementSampleKeepsThePresentedFieldUntilResume()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Opacity = 10 });
        Entity animation = PropertyAnimation.By(world, target, Opacity, 4f, Spec.Tween(1));
        PropertyAnimation.Pause(world, animation);
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);

        Assert.Equal(10, world.Read<Visual>(target).Opacity);
        Assert.True(world.IsEnabled<Timeline>(animation));
        PropertyAnimation.Resume(world, animation);
        Tick(systems, world, 0.5);
        Assert.Equal(12, world.Read<Visual>(target).Opacity);
    }

    [Fact]
    public void ChannelsAnimatePackedUnalignedFields()
    {
        using var world = new World();
        Channel<float> channel = Channel<float>.Of<PackedVisual>(static (ref PackedVisual visual) => ref visual.Opacity);
        Entity target = world.CreateEntity(new PackedVisual { Kind = 7, Opacity = 0.25f });
        PropertyAnimation.To(world, target, channel, 0.75f, Spec.Tween(1));
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);

        Assert.Equal((byte)7, world.Read<PackedVisual>(target).Kind);
        Assert.Equal(0.5f, world.Read<PackedVisual>(target).Opacity);
    }

    [Fact]
    public void ChannelsAnimateSparseFieldsAndAdvanceTheirVersions()
    {
        using var world = new World();
        Channel<float> channel = Channel<float>.Of<SparseVisual>(static (ref SparseVisual visual) => ref visual.Opacity);
        Entity target = world.CreateEntity();
        world.AddSparse(target, new SparseVisual { Number = 7, Opacity = 0.25f });
        PropertyAnimation.To(world, target, channel, 0.75f, Spec.Tween(1));
        uint before = world.SparseWriteVersion<SparseVisual>(target);
        using var systems = FloatSystems(world);

        Tick(systems, world, 0.5);

        Assert.Equal(7, world.ReadSparse<SparseVisual>(target).Number);
        Assert.Equal(0.5f, world.ReadSparse<SparseVisual>(target).Opacity);
        Assert.NotEqual(before, world.SparseWriteVersion<SparseVisual>(target));
    }

#if DEBUG
    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidSpeedsReportDevelopmentChecks(float speed)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));

        Assert.Throws<InvalidOperationException>(() => PropertyAnimation.SetSpeed(world, animation, speed));

        using var systems = FloatSystems(world);
        Tick(systems, world, 0.5);
        Assert.Equal(0.5f, world.Read<Visual>(target).Opacity);
    }

    [Fact]
    public void RetargetRequiresAnActiveChannelThroughDevelopmentChecks()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual());
        Entity animation = PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        Channel<float> other = Channel<float>.Of<PackedVisual>(static (ref PackedVisual visual) => ref visual.Opacity);
        Assert.Throws<InvalidOperationException>(() => PropertyAnimation.Retarget(world, animation, other, 2f));
        PropertyAnimation.Stop(world, animation);
        Assert.Throws<InvalidOperationException>(() => PropertyAnimation.Retarget(world, animation, Opacity, 2f));
    }
#endif

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PackedVisual : IComponent
    {
        public byte Kind;
        public float Opacity;
    }

    public struct SparseVisual : IComponent, ISparseComponent
    {
        public int Number;
        public float Opacity;
    }

    public readonly record struct Meter(float Value);

    public struct Reading : IComponent
    {
        public Meter Value;
    }

    public readonly struct MeterOps : IValueOps<Meter>
    {
        public static Meter Lerp(in Meter a, in Meter b, float t) => new(a.Value + (b.Value - a.Value) * t);
        public static Meter Add(in Meter a, in Meter b) => new(a.Value + b.Value);
        public static Meter Subtract(in Meter a, in Meter b) => new(a.Value - b.Value);
        public static Meter Scale(in Meter a, float scale) => new(a.Value * scale);
        public static float Distance(in Meter a, in Meter b) => MathF.Abs(a.Value - b.Value);
    }

    private static SystemGroup<ImmediateSystemContext> FloatSystems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var frame = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref frame);
    }

    public struct Visual : IComponent
    {
        public int Number;
        public float Opacity;
        public Vector2 Position;
    }
}
