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

    [Theory]
    [InlineData(FailureKind.OrdinaryCommand)]
    [InlineData(FailureKind.RelationshipValidation)]
    [InlineData(FailureKind.HierarchyValidation)]
    [InlineData(FailureKind.HookFault)]
    public void LaterFailure_DiscardsEveryCandidateOwnedMutation(FailureKind failure)
    {
        Fixture fixture = CreateFixture();
        InstallCandidateMutator(fixture);
        if (failure == FailureKind.HookFault)
        {
            fixture.World.Hooks<FaultProbe>().OnAdd(
                static (DeferredWorld _, Entity _, in FaultProbe _) =>
                    throw new AtomicHookFaultException());
        }

        LiveImage before = Capture(fixture);
        using var commands = new CommandBuffer(fixture.World);

        // The first two commands allocate and populate a candidate-only entity. The mutator hook
        // then changes every detached owner represented in LiveImage before the later command
        // fails. None of those allocator or owner mutations may become published.
        DeferredEntity candidateOnly = commands.CreateEntity();
        commands.Add(candidateOnly, new AtomicValue { Value = 404 });
        commands.Add(fixture.Subject, new CandidateMutator());
        switch (failure)
        {
            case FailureKind.OrdinaryCommand:
                commands.Replace(fixture.Spare, new NeverPresent { Value = 1 });
                break;

            case FailureKind.RelationshipValidation:
                _ = commands.Relations<AtomicLink>().Create(
                    fixture.Spare,
                    fixture.Spare,
                    new AtomicLink { Value = 2 });
                break;

            case FailureKind.HierarchyValidation:
                commands.Hierarchy<AtomicDomain>().SetParent(
                    fixture.Spare,
                    fixture.Spare,
                    HierarchyMaintenanceTiming.Immediate);
                break;

            case FailureKind.HookFault:
                commands.Add(fixture.Spare, new FaultProbe());
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Exception? error = Record.Exception(commands.Playback);

        Assert.NotNull(error);
        if (failure == FailureKind.HookFault)
            Assert.IsType<AtomicHookFaultException>(error);
        else
            Assert.IsType<InvalidOperationException>(error);
        Assert.False(candidateOnly.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => candidateOnly.Resolve());
        AssertPublishedImage(fixture, before);

        Entity next = fixture.World.CreateEntity();
        Assert.Equal(fixture.Edge.Entity.Index + 1, next.Index);
        Assert.Equal(0, next.Generation);
    }

    [Fact]
    public void SuccessfulPlayback_PublishesEveryCandidateOwnedMutationTogether()
    {
        Fixture fixture = CreateFixture();
        InstallCandidateMutator(fixture);
        int entityCountBefore = fixture.World.EntityCount;
        uint tickBefore = fixture.World.CurrentTick;
        long publicationEpochBefore = fixture.World.PublishedStructureEpoch;

        using var commands = new CommandBuffer(fixture.World);
        DeferredEntity created = commands.CreateEntity();
        commands.Add(created, new AtomicValue { Value = 77 });
        commands.Add(fixture.Subject, new CandidateMutator());

        commands.Playback();

        Entity liveCreated = created.Resolve();
        Assert.True(fixture.World.IsAlive(liveCreated));
        Assert.Equal(77, fixture.World.Read<AtomicValue>(liveCreated).Value);
        Assert.Equal(entityCountBefore + 1, fixture.World.EntityCount);
        Assert.True(fixture.World.Has<CandidateMutator>(fixture.Subject));
        Assert.Equal(101, fixture.World.Read<AtomicValue>(fixture.Subject).Value);
        Assert.Equal([901, 12, 13], ReadBuffer(fixture.World, fixture.Subject));
        Assert.Equal(202, fixture.World.ReadSparse<AtomicSparse>(fixture.Subject).Value);
        Assert.Equal(303, fixture.World.GetShared<AtomicShared>(fixture.Subject).Value);
        Assert.Equal(2, fixture.World.Read<AtomicIndex>(fixture.Subject).Key);
        Assert.Empty(fixture.World.GetByIndex<AtomicIndex, int>(1).ToArray());
        Assert.Equal(
            [fixture.Subject],
            fixture.World.GetByIndex<AtomicIndex, int>(2).ToArray());
        Assert.Equal(
            fixture.NewParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetParent(
                fixture.World,
                fixture.Subject));
        Assert.Empty(
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                fixture.World,
                fixture.OldParent));
        Assert.Equal(
            [fixture.Subject],
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                fixture.World,
                fixture.NewParent).ToArray());
        DirectedRelationEndpoints<AtomicLink> endpoints =
            fixture.World.GetDirectedRelationEndpoints(fixture.Edge);
        Assert.Equal(fixture.Subject, endpoints.Source);
        Assert.Equal(fixture.TargetB, endpoints.Target);
        Assert.Equal(tickBefore + 1, fixture.World.CurrentTick);
        Assert.Equal(publicationEpochBefore + 1, fixture.World.PublishedStructureEpoch);
    }

    [Fact]
    public void FailedPlayback_PreservesAllocatorFrontierFreeListOrderAndGenerations()
    {
        var world = new World(initialEntityCapacity: 4);
        Entity first = world.CreateEntity();
        Entity survivor = world.CreateEntity();
        Entity third = world.CreateEntity();
        world.DestroyEntity(first);
        world.DestroyEntity(third);

        int entityCountBefore = world.EntityCount;
        long publicationEpochBefore = world.PublishedStructureEpoch;
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
        Assert.Equal(publicationEpochBefore, world.PublishedStructureEpoch);
        Assert.True(world.IsAlive(survivor));
        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(third));

        // Destroy is LIFO: the candidate consumed third then first. A failed publication must
        // leave both the exact free-list order and each dead slot's generation untouched.
        Entity reusedThird = world.CreateEntity();
        Entity reusedFirst = world.CreateEntity();
        Entity frontier = world.CreateEntity();
        Assert.Equal(new Entity(third.Index, third.Generation + 1), reusedThird);
        Assert.Equal(new Entity(first.Index, first.Generation + 1), reusedFirst);
        Assert.Equal(new Entity(third.Index + 1, 0), frontier);
    }

    [Fact]
    public void DeferredHandles_AreUnresolvedInsideHookAndResolveOnlyAfterPublication()
    {
        var world = new World();
        DeferredEntity pendingSource = default;
        DeferredEntity pendingTarget = default;
        DeferredRelationEdge<AtomicLink> pendingEdge = default;
        bool hookRan = false;
        long publicationEpochBefore = world.PublishedStructureEpoch;
        long hookObservedEpoch = -1;

        world.Hooks<HandleProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity entity, in HandleProbe probe) =>
            {
                _ = probe;
                hookRan = true;
                hookObservedEpoch = world.PublishedStructureEpoch;
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
        Assert.Equal(publicationEpochBefore, hookObservedEpoch);
        Assert.Equal(publicationEpochBefore + 1, world.PublishedStructureEpoch);
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
        long failedPublicationEpoch = world.PublishedStructureEpoch;

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
        Assert.Equal(failedPublicationEpoch, world.PublishedStructureEpoch);
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

        Assert.Equal(failedPublicationEpoch + 1, world.PublishedStructureEpoch);
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
        Assert.Contains("sealed", sealedError.Message, StringComparison.OrdinalIgnoreCase);

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

    [Fact]
    public void PublishedSnapshotSpans_RemainStableAcrossSuccessAndFailure()
    {
        Fixture fixture = CreateFixture();
        InstallCandidateMutator(fixture);

        HierarchyChildrenSnapshot<AtomicDomain> originalChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                fixture.World,
                fixture.OldParent);
        RelationAdjacencySnapshot<AtomicLink> originalRelations =
            fixture.World.GetOutgoingRelations<AtomicLink>(fixture.Subject);
        ReadOnlySpan<Entity> originalIndex =
            fixture.World.GetByIndex<AtomicIndex, int>(1);

        using (var successful = new CommandBuffer(fixture.World))
        {
            successful.Add(fixture.Subject, new CandidateMutator());
            successful.Playback();
        }

        Assert.Equal([fixture.Subject], originalChildren.Span.ToArray());
        Assert.Equal(1, originalRelations.Count);
        Assert.Equal(fixture.Edge, originalRelations.Entries[0].Edge);
        Assert.Equal(fixture.TargetA, originalRelations.Entries[0].OtherEndpoint);
        Assert.Equal([fixture.Subject], originalIndex.ToArray());

        HierarchyChildrenSnapshot<AtomicDomain> publishedChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                fixture.World,
                fixture.NewParent);
        RelationAdjacencySnapshot<AtomicLink> publishedRelations =
            fixture.World.GetOutgoingRelations<AtomicLink>(fixture.Subject);
        ReadOnlySpan<Entity> publishedIndex =
            fixture.World.GetByIndex<AtomicIndex, int>(2);

        using (var failed = new CommandBuffer(fixture.World))
        {
            failed.DestroyEntity(fixture.Subject);
            failed.Hierarchy<AtomicDomain>().SetParent(
                fixture.Spare,
                fixture.Spare,
                HierarchyMaintenanceTiming.Immediate);
            Assert.Throws<InvalidOperationException>(failed.Playback);
        }

        Assert.Equal([fixture.Subject], originalChildren.Span.ToArray());
        Assert.Equal(fixture.TargetA, originalRelations.Entries[0].OtherEndpoint);
        Assert.Equal([fixture.Subject], originalIndex.ToArray());
        Assert.Equal([fixture.Subject], publishedChildren.Span.ToArray());
        Assert.Equal(1, publishedRelations.Count);
        Assert.Equal(fixture.Edge, publishedRelations.Entries[0].Edge);
        Assert.Equal(fixture.TargetB, publishedRelations.Entries[0].OtherEndpoint);
        Assert.Equal([fixture.Subject], publishedIndex.ToArray());
        Assert.Equal(
            publishedChildren.Generation,
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                fixture.World,
                fixture.NewParent).Generation);
        Assert.Equal(
            publishedRelations.Generation,
            fixture.World.GetOutgoingRelations<AtomicLink>(fixture.Subject).Generation);
    }

    [Fact]
    public async Task ReaderDuringBlockedCandidate_SeesOnlyOldPublishedRootUntilPublication()
    {
        Fixture fixture = CreateFixture();
        InstallCandidateMutator(fixture);
        LiveImage before = Capture(fixture);

        using var hookEntered = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerFinished = new ManualResetEventSlim();
        fixture.World.Hooks<BlockingPublicationProbe>().OnAdd(
            (DeferredWorld _, Entity _, in BlockingPublicationProbe _) =>
            {
                hookEntered.Set();
                if (!releaseHook.Wait(ThreadTimeout))
                    throw new TimeoutException("Test did not release the candidate hook.");
            });

        using var commands = new CommandBuffer(fixture.World);
        DeferredEntity candidateOnly = commands.CreateEntity();
        commands.Add(candidateOnly, new AtomicValue { Value = 707 });
        commands.Add(fixture.Subject, new CandidateMutator());
        commands.Add(fixture.Spare, new BlockingPublicationProbe());
        Task playback = Task.Run(commands.Playback);
        Task<LiveImage>? reader = null;
        bool entered = hookEntered.Wait(ThreadTimeout);
        bool readerCompletedBeforePublication = false;

        if (entered)
        {
            reader = Task.Run(() =>
            {
                readerStarted.Set();
                LiveImage result = Capture(fixture);
                readerFinished.Set();
                return result;
            });

            _ = readerStarted.Wait(ThreadTimeout);
            readerCompletedBeforePublication = readerFinished.Wait(ThreadTimeout);
        }

        releaseHook.Set();
        Exception? playbackError = null;
        try
        {
            await playback;
        }
        catch (Exception error)
        {
            playbackError = error;
        }

        LiveImage? observed = reader is null ? null : await reader;

        Assert.True(entered, "Playback never entered the blocking candidate hook.");
        Assert.True(readerCompletedBeforePublication,
            "The reader did not complete while the candidate hook still blocked publication.");
        Assert.Null(playbackError);
        Assert.NotNull(observed);
        AssertImagesEqual(before, observed!);

        Assert.Equal(before.StructureEpoch + 1, fixture.World.PublishedStructureEpoch);
        Assert.Equal(101, fixture.World.Read<AtomicValue>(fixture.Subject).Value);
        Assert.True(fixture.World.Has<CandidateMutator>(fixture.Subject));
        Assert.True(candidateOnly.IsResolved);
        Entity publishedEntity = candidateOnly.Resolve();
        Assert.True(fixture.World.IsAlive(publishedEntity));
        Assert.Equal(707, fixture.World.Read<AtomicValue>(publishedEntity).Value);
    }

    private static Fixture CreateFixture()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity subject = world.CreateEntity(new AtomicValue { Value = 10 });
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        Entity spare = world.CreateEntity();

        world.Add(subject, new AtomicIndex { Key = 1 });
        world.AddBuffer<AtomicBufferElement>(subject);
        world.ExecuteBufferWrite<AtomicBufferElement>(subject, static buffer =>
        {
            buffer.Add(new AtomicBufferElement { Value = 11 });
            buffer.Add(new AtomicBufferElement { Value = 12 });
            buffer.Add(new AtomicBufferElement { Value = 13 });
        });
        world.AddSparse(subject, new AtomicSparse { Value = 20 });
        world.AddShared(subject, new AtomicShared { Value = 30 });
        SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.SetParent(
            world,
            subject,
            oldParent);
        RelationEdge<AtomicLink> edge = world.CreateRelation(
            subject,
            targetA,
            new AtomicLink { Value = 40 });

        _ = world.GetByIndex<AtomicIndex, int>(1);
        _ = world.GetByIndex<AtomicIndex, int>(2);
        _ = world.AcquireSystemTick();
        return new Fixture(
            world,
            subject,
            oldParent,
            newParent,
            targetA,
            targetB,
            spare,
            edge);
    }

    private static void InstallCandidateMutator(Fixture fixture)
    {
        fixture.World.Hooks<CandidateMutator>().OnAdd(
            (DeferredWorld _, Entity entity, in CandidateMutator _) =>
            {
                Assert.Equal(fixture.Subject, entity);
                fixture.World.Components.Replace(
                    fixture.Subject,
                    new AtomicValue { Value = 101 });
                DynamicBuffer<AtomicBufferElement> buffer =
                    fixture.World.Buffers.BorrowWrite<AtomicBufferElement>(fixture.Subject);
                buffer[0] = new AtomicBufferElement { Value = 901 };
                fixture.World.Sparse.Replace(
                    fixture.Subject,
                    new AtomicSparse { Value = 202 });
                fixture.World.Shared.Replace(
                    fixture.Subject,
                    new AtomicShared { Value = 303 });
                fixture.World.Components.Replace(
                    fixture.Subject,
                    new AtomicIndex { Key = 2 });
                fixture.World.Hierarchy.Domain<AtomicDomain>().SetParent(
                    fixture.Subject,
                    fixture.NewParent,
                    insertIndex: null,
                    immediate: true);
                fixture.World.RelationGraph.Retarget(
                    fixture.World,
                    fixture.Edge,
                    fixture.Subject,
                    fixture.TargetB,
                    RelationMaintenanceTiming.Immediate);
                _ = fixture.World.Clock.Acquire();
            });
    }

    private static LiveImage Capture(Fixture fixture)
    {
        World world = fixture.World;
        HierarchyChildrenSnapshot<AtomicDomain> oldChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                world,
                fixture.OldParent);
        HierarchyChildrenSnapshot<AtomicDomain> newChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                world,
                fixture.NewParent);
        RelationAdjacencySnapshot<AtomicLink> relations =
            world.GetOutgoingRelations<AtomicLink>(fixture.Subject);
        DirectedRelationEndpoints<AtomicLink> endpoints =
            world.GetDirectedRelationEndpoints(fixture.Edge);

        return new LiveImage
        {
            EntityCount = world.EntityCount,
            HasCandidateMutator = world.Has<CandidateMutator>(fixture.Subject),
            Value = world.Read<AtomicValue>(fixture.Subject).Value,
            Buffer = ReadBuffer(world, fixture.Subject),
            Sparse = world.ReadSparse<AtomicSparse>(fixture.Subject).Value,
            Shared = world.GetShared<AtomicShared>(fixture.Subject).Value,
            IndexKey = world.Read<AtomicIndex>(fixture.Subject).Key,
            IndexOne = world.GetByIndex<AtomicIndex, int>(1).ToArray(),
            IndexTwo = world.GetByIndex<AtomicIndex, int>(2).ToArray(),
            Parent = SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetParent(
                world,
                fixture.Subject),
            OldChildren = oldChildren.ToArray(),
            OldChildrenGeneration = oldChildren.Generation,
            NewChildren = newChildren.ToArray(),
            NewChildrenGeneration = newChildren.Generation,
            RelationSource = endpoints.Source,
            RelationTarget = endpoints.Target,
            RelationGeneration = relations.Generation,
            Relations = Observe(relations),
            Tick = world.CurrentTick,
            StructureEpoch = world.PublishedStructureEpoch,
        };
    }

    private static void AssertPublishedImage(Fixture fixture, LiveImage expected)
    {
        World world = fixture.World;
        Assert.Equal(expected.EntityCount, world.EntityCount);
        Assert.True(world.IsAlive(fixture.Subject));
        Assert.True(world.IsAlive(fixture.Edge.Entity));
        Assert.False(world.Has<CandidateMutator>(fixture.Subject));
        Assert.False(world.Has<FaultProbe>(fixture.Spare));
        Assert.Equal(expected.Value, world.Read<AtomicValue>(fixture.Subject).Value);
        Assert.Equal(expected.Buffer, ReadBuffer(world, fixture.Subject));
        Assert.Equal(expected.Sparse, world.ReadSparse<AtomicSparse>(fixture.Subject).Value);
        Assert.Equal(expected.Shared, world.GetShared<AtomicShared>(fixture.Subject).Value);
        Assert.Equal(expected.IndexKey, world.Read<AtomicIndex>(fixture.Subject).Key);
        Assert.Equal(expected.IndexOne, world.GetByIndex<AtomicIndex, int>(1).ToArray());
        Assert.Equal(expected.IndexTwo, world.GetByIndex<AtomicIndex, int>(2).ToArray());
        Assert.Equal(
            expected.Parent,
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetParent(
                world,
                fixture.Subject));

        HierarchyChildrenSnapshot<AtomicDomain> oldChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                world,
                fixture.OldParent);
        HierarchyChildrenSnapshot<AtomicDomain> newChildren =
            SomeEngine.ECS.Hierarchy.Hierarchy<AtomicDomain>.GetChildren(
                world,
                fixture.NewParent);
        Assert.Equal(expected.OldChildren, oldChildren.ToArray());
        Assert.Equal(expected.OldChildrenGeneration, oldChildren.Generation);
        Assert.Equal(expected.NewChildren, newChildren.ToArray());
        Assert.Equal(expected.NewChildrenGeneration, newChildren.Generation);

        DirectedRelationEndpoints<AtomicLink> endpoints =
            world.GetDirectedRelationEndpoints(fixture.Edge);
        RelationAdjacencySnapshot<AtomicLink> relations =
            world.GetOutgoingRelations<AtomicLink>(fixture.Subject);
        Assert.Equal(expected.RelationSource, endpoints.Source);
        Assert.Equal(expected.RelationTarget, endpoints.Target);
        Assert.Equal(expected.RelationGeneration, relations.Generation);
        Assert.Equal(expected.Relations, Observe(relations));
        Assert.Equal(expected.Tick, world.CurrentTick);
        Assert.Equal(expected.StructureEpoch, world.PublishedStructureEpoch);
    }

    private static void AssertImagesEqual(LiveImage expected, LiveImage actual)
    {
        Assert.Equal(expected.EntityCount, actual.EntityCount);
        Assert.Equal(expected.HasCandidateMutator, actual.HasCandidateMutator);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Buffer, actual.Buffer);
        Assert.Equal(expected.Sparse, actual.Sparse);
        Assert.Equal(expected.Shared, actual.Shared);
        Assert.Equal(expected.IndexKey, actual.IndexKey);
        Assert.Equal(expected.IndexOne, actual.IndexOne);
        Assert.Equal(expected.IndexTwo, actual.IndexTwo);
        Assert.Equal(expected.Parent, actual.Parent);
        Assert.Equal(expected.OldChildren, actual.OldChildren);
        Assert.Equal(expected.OldChildrenGeneration, actual.OldChildrenGeneration);
        Assert.Equal(expected.NewChildren, actual.NewChildren);
        Assert.Equal(expected.NewChildrenGeneration, actual.NewChildrenGeneration);
        Assert.Equal(expected.RelationSource, actual.RelationSource);
        Assert.Equal(expected.RelationTarget, actual.RelationTarget);
        Assert.Equal(expected.RelationGeneration, actual.RelationGeneration);
        Assert.Equal(expected.Relations, actual.Relations);
        Assert.Equal(expected.Tick, actual.Tick);
        Assert.Equal(expected.StructureEpoch, actual.StructureEpoch);
    }

    private static int[] ReadBuffer(World world, Entity entity)
    {
        int[] values = [];
        world.ExecuteBufferRead<AtomicBufferElement>(
            entity,
            buffer => values = buffer.AsSpan().ToArray().Select(static item => item.Value).ToArray());
        return values;
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

    public enum FailureKind
    {
        OrdinaryCommand,
        RelationshipValidation,
        HierarchyValidation,
        HookFault,
    }

    private sealed record Fixture(
        World World,
        Entity Subject,
        Entity OldParent,
        Entity NewParent,
        Entity TargetA,
        Entity TargetB,
        Entity Spare,
        RelationEdge<AtomicLink> Edge);

    private sealed class LiveImage
    {
        public required int EntityCount { get; init; }
        public required bool HasCandidateMutator { get; init; }
        public required int Value { get; init; }
        public required int[] Buffer { get; init; }
        public required int Sparse { get; init; }
        public required int Shared { get; init; }
        public required int IndexKey { get; init; }
        public required Entity[] IndexOne { get; init; }
        public required Entity[] IndexTwo { get; init; }
        public required Entity Parent { get; init; }
        public required Entity[] OldChildren { get; init; }
        public required ulong OldChildrenGeneration { get; init; }
        public required Entity[] NewChildren { get; init; }
        public required ulong NewChildrenGeneration { get; init; }
        public required Entity RelationSource { get; init; }
        public required Entity RelationTarget { get; init; }
        public required uint RelationGeneration { get; init; }
        public required RelationObservation[] Relations { get; init; }
        public required uint Tick { get; init; }
        public required long StructureEpoch { get; init; }
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

    private struct CandidateMutator : SomeEngine.ECS.IComponent;

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
    private struct AtomicBufferElement : IBufferElement
    {
        public int Value;
    }

    private struct AtomicSparse : ISparseComponent
    {
        public int Value;
    }

    private struct AtomicShared : ISharedComponent, IEquatable<AtomicShared>
    {
        public int Value;

        public readonly bool Equals(AtomicShared other) => Value == other.Value;

        public override readonly bool Equals(object? obj) =>
            obj is AtomicShared other && Equals(other);

        public override readonly int GetHashCode() => Value;
    }

    private struct AtomicIndex : IIndexedComponent<int>
    {
        public int Key;

        public readonly int GetKey() => Key;
    }

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
