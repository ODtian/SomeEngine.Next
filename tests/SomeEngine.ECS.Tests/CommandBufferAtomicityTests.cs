using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class CommandBufferAtomicityTests
{
    private static readonly TimeSpan ThreadTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void FailedPlaybackPreservesLiveEntitiesAndInvalidatesCreatedHandles()
    {
        var world = new World(initialEntityCapacity: 4);
        Entity first = world.CreateEntity();
        Entity survivor = world.CreateEntity();
        Entity third = world.CreateEntity();
        world.DestroyEntity(first);
        world.DestroyEntity(third);

        int entityCountBefore = world.EntityCount;
        using (var commands = new CommandBuffer(world))
        {
            DeferredEntity candidateFreeHead = commands.CreateEntity();
            DeferredEntity candidateFreeTail = commands.CreateEntity();
            commands.Add(candidateFreeHead, new AtomicValue { Value = 31 });
            commands.Add(candidateFreeTail, new AtomicValue { Value = 32 });
            commands.Replace(survivor, new NeverPresent { Value = 1 });

            Assert.Throws<InvalidOperationException>(commands.Playback);
            Assert.False(candidateFreeHead.TryResolve(out _));
            Assert.False(candidateFreeTail.TryResolve(out _));
        }

        Assert.Equal(entityCountBefore, world.EntityCount);
        Assert.True(world.IsAlive(survivor));
        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(third));

        // Destroy is LIFO: the candidate consumed third then first. A failed publication must
        // leave both the exact free-list order and each dead slot's generation untouched.
        Entity reusedThird = world.CreateEntity();
        Entity reusedFirst = world.CreateEntity();
        Entity frontier = world.CreateEntity();
        Assert.True(world.IsAlive(reusedThird));
        Assert.NotEqual(third, reusedThird);
        Assert.True(world.IsAlive(reusedFirst));
        Assert.NotEqual(first, reusedFirst);
        Assert.True(world.IsAlive(frontier));
        Assert.NotEqual(reusedFirst, frontier);
        Assert.NotEqual(reusedThird, frontier);
    }

    [Fact]
    public void DeferredHandles_AreUnresolvedInsideHookAndResolveOnlyAfterPublication()
    {
        var world = new World();
        DeferredEntity pendingSource = default;
        DeferredEntity pendingTarget = default;
        DeferredRelationEdge<AtomicLink> pendingEdge = default;
        bool hookRan = false;

        world.Hooks<HandleProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity entity, in HandleProbe probe) =>
            {
                _ = probe;
                hookRan = true;
                Assert.True(hookWorld.IsAlive(entity));
                Assert.False(pendingSource.IsResolved);
                Assert.False(pendingTarget.IsResolved);
                Assert.False(pendingEdge.IsResolved);
                Assert.False(pendingSource.TryResolve(out _));
                Assert.False(pendingTarget.TryResolve(out _));
                Assert.False(pendingEdge.TryResolve(out _));
                Assert.Throws<InvalidOperationException>(() => pendingSource.Resolve());
                Assert.Throws<InvalidOperationException>(() => pendingTarget.Resolve());
                Assert.Throws<InvalidOperationException>(() => pendingEdge.Resolve());
            });

        using var commands = new CommandBuffer(world);
        pendingSource = commands.CreateEntity();
        pendingTarget = commands.CreateEntity();
        pendingEdge = commands.Relations<AtomicLink>().Create(
            pendingSource,
            pendingTarget,
            new AtomicLink { Value = 44 });
        commands.Add(pendingSource, new HandleProbe());

        commands.Playback();

        Assert.True(hookRan);
        Assert.True(pendingSource.IsResolved);
        Assert.True(pendingTarget.IsResolved);
        Assert.True(pendingEdge.IsResolved);
        Entity source = pendingSource.Resolve();
        Entity target = pendingTarget.Resolve();
        RelationEdge<AtomicLink> edge = pendingEdge.Resolve();
        Assert.True(world.IsAlive(source));
        Assert.True(world.IsAlive(target));
        Assert.True(world.IsAlive(edge.Entity));
        DirectedRelationEndpoints<AtomicLink> endpoints = world.GetDirectedRelationEndpoints(edge);
        Assert.Equal(source, endpoints.Source);
        Assert.Equal(target, endpoints.Target);
    }

    [Fact]
    public void FailedDeferredHandles_RemainPermanentlyInvalidAfterLaterPublication()
    {
        var world = new World();
        DeferredEntity failedSource = default;
        DeferredEntity failedTarget = default;
        DeferredRelationEdge<AtomicLink> failedEdge = default;
        bool hookObservedUnpublishedHandles = false;

        world.Hooks<HandleFaultProbe>().OnAdd(
            (DeferredWorld _, Entity _, in HandleFaultProbe _) =>
            {
                hookObservedUnpublishedHandles =
                    !failedSource.IsResolved &&
                    !failedTarget.IsResolved &&
                    !failedEdge.IsResolved;
                throw new AtomicHookFaultException();
            });

        using (var commands = new CommandBuffer(world))
        {
            failedSource = commands.CreateEntity();
            failedTarget = commands.CreateEntity();
            failedEdge = commands.Relations<AtomicLink>().Create(
                failedSource,
                failedTarget,
                new AtomicLink { Value = 55 });
            commands.Add(failedSource, new HandleFaultProbe());

            Assert.Throws<AtomicHookFaultException>(commands.Playback);
        }

        Assert.True(hookObservedUnpublishedHandles);
        Assert.False(failedSource.TryResolve(out _));
        Assert.False(failedTarget.TryResolve(out _));
        Assert.False(failedEdge.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => failedSource.Resolve());
        Assert.Throws<InvalidOperationException>(() => failedTarget.Resolve());
        Assert.Throws<InvalidOperationException>(() => failedEdge.Resolve());

        using (var successful = new CommandBuffer(world))
        {
            DeferredEntity later = successful.CreateEntity();
            successful.Playback();
            Assert.True(later.IsResolved);
        }

        Assert.False(failedSource.TryResolve(out _));
        Assert.False(failedTarget.TryResolve(out _));
        Assert.False(failedEdge.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => failedSource.Resolve());
        Assert.Throws<InvalidOperationException>(() => failedTarget.Resolve());
        Assert.Throws<InvalidOperationException>(() => failedEdge.Resolve());
    }

    [Fact]
    public void WorldFlushPreservesPublishedDeferredEntityAndRelationIdentities()
    {
        var world = new World();
        Entity target = world.CreateEntity();
        CommandBuffer commands = world.Commands();
        DeferredEntity source = commands.CreateEntity();
        DeferredRelationEdge<AtomicLink> edge = commands.Relations<AtomicLink>().Create(
            source,
            target,
            new AtomicLink { Value = 7 });

        world.Flush();

        Assert.True(source.TryResolve(out Entity liveSource));
        Assert.True(world.IsAlive(liveSource));
        Assert.True(edge.TryResolve(out RelationEdge<AtomicLink> liveEdge));
        Assert.True(world.IsAlive(liveEdge.Entity));
        Assert.Equal(liveSource, source.Resolve());
        Assert.Equal(liveEdge, edge.Resolve());
    }

    [Fact]
    public void PublishedHookWaveSealsOldWorldBufferAndReacquireRecordsALaterWave()
    {
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity earlierDestination = world.CreateEntity();
        Entity hookDestination = world.CreateEntity();
        Entity laterDestination = world.CreateEntity();
        CommandBuffer oldWave = world.Commands();
        oldWave.Add(earlierDestination, new WaveResult { Value = 0 });
        world.Hooks<WaveTrigger>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in WaveTrigger _) =>
                hookWorld.Commands().Add(hookDestination, new WaveResult { Value = 1 }));

        using (var triggerPlayback = new CommandBuffer(world))
        {
            triggerPlayback.Add(trigger, new WaveTrigger { Value = 7 });
            triggerPlayback.Playback();
        }

        InvalidOperationException sealedError = Assert.Throws<InvalidOperationException>(() =>
            oldWave.Add(laterDestination, new WaveResult { Value = 2 }));

        CommandBuffer laterWave = world.Commands();
        laterWave.Add(laterDestination, new WaveResult { Value = 3 });

        world.Flush();
        Assert.Equal(0, world.Read<WaveResult>(earlierDestination).Value);
        Assert.False(world.Has<WaveResult>(hookDestination));
        Assert.False(world.Has<WaveResult>(laterDestination));

        world.Flush();
        Assert.Equal(1, world.Read<WaveResult>(hookDestination).Value);
        Assert.False(world.Has<WaveResult>(laterDestination));

        world.Flush();
        Assert.Equal(3, world.Read<WaveResult>(laterDestination).Value);
    }

    [Fact]
    public void HookCommands_AreASeparateWaveExecutedByTheNextFlush()
    {
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        world.Hooks<WaveTrigger>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in WaveTrigger value) =>
            {
                DeferredCommandWriter nextWave = hookWorld.Commands();
                nextWave.Add(
                    destination,
                    new WaveResult { Value = value.Value + 1 });
            });

        world.Commands().Add(trigger, new WaveTrigger { Value = 70 });

        world.Flush();

        Assert.True(world.Has<WaveTrigger>(trigger));
        Assert.False(world.Has<WaveResult>(destination));

        world.Flush();

        Assert.True(world.Has<WaveResult>(destination));
        Assert.Equal(71, world.Read<WaveResult>(destination).Value);
    }

    [Fact]
    public void HookCommandOverlay_IsDiscardedWhenCurrentWaveFails()
    {
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        DeferredEntity discardedOverlayEntity = default;
        bool hookRan = false;
        world.Hooks<FailingWaveTrigger>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in FailingWaveTrigger _) =>
            {
                hookRan = true;
                DeferredCommandWriter discardedOverlay = hookWorld.Commands();
                discardedOverlayEntity = discardedOverlay.CreateEntity();
                discardedOverlay.Add(destination, new WaveResult { Value = 99 });
                throw new AtomicHookFaultException();
            });

        world.Commands().Add(trigger, new FailingWaveTrigger());

        Assert.Throws<AtomicHookFaultException>(world.Flush);
        Assert.True(hookRan);
        Assert.False(world.Has<FailingWaveTrigger>(trigger));
        Assert.False(world.Has<WaveResult>(destination));
        Assert.False(discardedOverlayEntity.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => discardedOverlayEntity.Resolve());

        world.Flush();

        Assert.False(world.Has<WaveResult>(destination));
        Assert.Equal(2, world.EntityCount);
    }

    private static RelationObservation[] Observe(
        RelationAdjacencySnapshot<AtomicLink> snapshot)
    {
        var observations = new RelationObservation[snapshot.Count];
        for (int index = 0; index < snapshot.Count; index++)
        {
            RelationAdjacencyEntry<AtomicLink> entry = snapshot.Entries[index];
            observations[index] = new RelationObservation(
                entry.Edge.Entity,
                entry.OtherEndpoint);
        }

        return observations;
    }

    private readonly record struct RelationObservation(Entity Edge, Entity OtherEndpoint);

    private readonly struct AtomicDomain : IHierarchyDomain;

    private struct AtomicValue : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct NeverPresent : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct FaultProbe : SomeEngine.ECS.IComponent;

    private struct HandleProbe : SomeEngine.ECS.IComponent;

    private struct HandleFaultProbe : SomeEngine.ECS.IComponent;

    private struct WaveTrigger : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct FailingWaveTrigger : SomeEngine.ECS.IComponent;

    private struct BlockingPublicationProbe : SomeEngine.ECS.IComponent;

    private struct WaveResult : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    [BufferCapacity(1)]
    [RelationSchema(
        RelationDirection.Directed,
        RelationCardinality.UniqueTarget,
        AllowSelfEdge = false)]
    private struct AtomicLink : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private sealed class AtomicHookFaultException : Exception;
}
