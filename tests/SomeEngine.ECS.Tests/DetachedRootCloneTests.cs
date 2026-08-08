using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedRootCloneTests
{
    [Fact]
    public void WorldRootFork_BufferOverflowDetachesOnlyTheWrittenRowAndOnlyOncePerGeneration()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new DetachedValue { Value = 1 });
        Entity untouchedEntity = world.CreateEntity(new DetachedValue { Value = 2 });
        world.AddBuffer<DetachedBufferElement>(entity);
        world.AddBuffer<DetachedBufferElement>(untouchedEntity);
        world.AddShared(entity, new DetachedBucket(7));
        world.AddShared(untouchedEntity, new DetachedBucket(7));
        world.ExecuteBufferWrite<DetachedBufferElement>(entity, static buffer =>
        {
            for (int index = 0; index < 64; index++)
                buffer.Add(new DetachedBufferElement { Value = 100 + index });
        });
        world.ExecuteBufferWrite<DetachedBufferElement>(untouchedEntity, static buffer =>
        {
            for (int index = 0; index < 64; index++)
                buffer.Add(new DetachedBufferElement { Value = 200 + index });
        });

        WorldStructureRoot published = world.PublishedStructureRoot;
        EntityRecord publishedRecord = published.Entities.Store.GetRecordReadOnly(entity);
        EntityRecord publishedUntouchedRecord =
            published.Entities.Store.GetRecordReadOnly(untouchedEntity);
        Chunk publishedChunk = publishedRecord.Chunk!;
        Assert.Same(publishedChunk, publishedUntouchedRecord.Chunk);
        int headerColumn = publishedRecord.Archetype!.Column(
            BufferComponents.Header<DetachedBufferElement>());
        object publishedBacking = publishedChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
            headerColumn,
            publishedRecord.RowInChunk)!;
        object publishedUntouchedBacking =
            publishedChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                publishedUntouchedRecord.RowInChunk)!;

        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);
        EntityRecord candidateRecord = candidate.Entities.Store.GetRecordReadOnly(entity);
        EntityRecord candidateUntouchedRecord =
            candidate.Entities.Store.GetRecordReadOnly(untouchedEntity);
        Chunk candidateChunk = candidateRecord.Chunk!;

        Assert.NotSame(publishedChunk, candidateChunk);
        Assert.True(publishedChunk.SharesStorageWith(candidateChunk));
        Assert.Same(candidateChunk, candidateUntouchedRecord.Chunk);
        Assert.Same(publishedChunk.SharedValues, candidateChunk.SharedValues);

        candidate.Components.Replace(entity, new DetachedValue { Value = 2 });
        Assert.False(publishedChunk.SharesStorageWith(candidateChunk));
        Assert.Equal(0, candidateChunk.BufferOverflowDetachCount);
        Assert.Same(
            publishedBacking,
            candidateChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                candidateRecord.RowInChunk));
        Assert.Same(
            publishedUntouchedBacking,
            candidateChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                candidateUntouchedRecord.RowInChunk));
        Assert.Same(publishedChunk.SharedValues, candidateChunk.SharedValues);

        DynamicBuffer<DetachedBufferElement> writable =
            candidate.Buffers.BorrowWrite<DetachedBufferElement>(entity, writeVersion: 71);
        writable[0] = new DetachedBufferElement { Value = 99 };

        object candidateBacking = candidateChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
            headerColumn,
            candidateRecord.RowInChunk)!;
        Assert.NotSame(publishedBacking, candidateBacking);
        Assert.Same(
            publishedUntouchedBacking,
            candidateChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                candidateUntouchedRecord.RowInChunk));
        Assert.Equal(1, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(71u, candidateChunk.ChangeVersions[headerColumn]);
        Assert.Equal(100, published.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
        Assert.Equal(99, candidate.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
        Assert.Equal(200, candidate.Buffers.BorrowRead<DetachedBufferElement>(untouchedEntity)[0].Value);

        writable[1] = new DetachedBufferElement { Value = 98 };
        Assert.Same(
            candidateBacking,
            candidateChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                candidateRecord.RowInChunk));
        Assert.Equal(1, candidateChunk.BufferOverflowDetachCount);

        // Treat the candidate as the next published generation. A further fork shares its latest
        // immutable backing, then detaches exactly once without writing through either ancestor.
        WorldStructureRoot next = candidate.CloneDetached(world, world.HookStore);
        EntityRecord nextRecord = next.Entities.Store.GetRecordReadOnly(entity);
        Chunk nextChunk = nextRecord.Chunk!;
        Assert.True(candidateChunk.SharesStorageWith(nextChunk));
        DynamicBuffer<DetachedBufferElement> nextWritable =
            next.Buffers.BorrowWrite<DetachedBufferElement>(entity, writeVersion: 72);
        nextWritable[0] = new DetachedBufferElement { Value = 97 };
        Assert.Equal(1, nextChunk.BufferOverflowDetachCount);
        Assert.Equal(99, candidate.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
        Assert.Equal(97, next.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
        Assert.Equal(100, published.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
    }

    [Fact]
    public void WorldRootFork_ReadOnlyLocationResolutionDoesNotDetachEntityRecordPage()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new DetachedValue { Value = 7 });
        world.AddBuffer<DetachedBufferElement>(entity);
        world.ExecuteBufferWrite<DetachedBufferElement>(
            entity,
            static buffer => buffer.Add(new DetachedBufferElement { Value = 13 }));

        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);
        Assert.True(published.Entities.Store.SharesRecordPageWith(
            candidate.Entities.Store,
            entity.Index));

        Assert.True(candidate.Components.Has<DetachedValue>(entity));
        Assert.Equal(7, candidate.Components.Read<DetachedValue>(entity).Value);
        Assert.Equal(13, candidate.Buffers.BorrowRead<DetachedBufferElement>(entity)[0].Value);
        Assert.True(published.Entities.Store.SharesRecordPageWith(
            candidate.Entities.Store,
            entity.Index));

        candidate.Components.Replace(entity, new DetachedValue { Value = 9 });
        Assert.Equal(9, candidate.Components.Read<DetachedValue>(entity).Value);
        Assert.Equal(7, published.Components.Read<DetachedValue>(entity).Value);
        Assert.True(published.Entities.Store.SharesRecordPageWith(
            candidate.Entities.Store,
            entity.Index));
    }

    [Fact]
    public void WorldRootFork_ClearAndRegrowDoNotClearInheritedManagedOverflow()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        world.AddBuffer<ManagedDetachedBufferElement>(entity);
        world.ExecuteBufferWrite<ManagedDetachedBufferElement>(entity, static buffer =>
        {
            buffer.Add(new ManagedDetachedBufferElement { Value = "published-a" });
            buffer.Add(new ManagedDetachedBufferElement { Value = "published-b" });
            buffer.Add(new ManagedDetachedBufferElement { Value = "published-c" });
        });

        WorldStructureRoot published = world.PublishedStructureRoot;
        EntityRecord publishedRecord = published.Entities.Store.GetRecordReadOnly(entity);
        Chunk publishedChunk = publishedRecord.Chunk!;
        int inlineColumn = publishedRecord.Archetype!.Column(
            BufferComponents.Inline<ManagedDetachedBufferElement>());
        DynamicBufferInline<ManagedDetachedBufferElement> promotedInline =
            publishedChunk.ReadComponent<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        Assert.Null(promotedInline[0].Value);

        ref DynamicBufferInline<ManagedDetachedBufferElement> publishedInline =
            ref publishedChunk.GetComponentRef<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        publishedInline[0] = new ManagedDetachedBufferElement
        {
            Value = "published-stale-inline",
        };

        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);
        Chunk candidateChunk = candidate.Entities.Store.GetRecordReadOnly(entity).Chunk!;
        DynamicBuffer<ManagedDetachedBufferElement> candidateBuffer =
            candidate.Buffers.BorrowWrite<ManagedDetachedBufferElement>(entity, writeVersion: 81);

        WorldStructureRoot loadCandidate = published.CloneDetached(world, world.HookStore);
        Chunk loadCandidateChunk = loadCandidate.Entities.Store.GetRecordReadOnly(entity).Chunk!;
        DynamicBuffer<ManagedDetachedBufferElement> loadCandidateBuffer =
            loadCandidate.Buffers.BorrowWrite<ManagedDetachedBufferElement>(
                entity,
                writeVersion: 82);

        candidateBuffer.Clear();
        Assert.Equal(0, loadCandidateBuffer.LoadUninitialized(0).Length);

        Assert.Equal(0, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(0, loadCandidateChunk.BufferOverflowDetachCount);
        Assert.Equal(0, candidate.Buffers.BorrowRead<ManagedDetachedBufferElement>(entity).Count);
        Assert.Equal(0, loadCandidate.Buffers.BorrowRead<ManagedDetachedBufferElement>(entity).Count);
        DynamicBufferInline<ManagedDetachedBufferElement> clearedInline =
            candidateChunk.ReadComponent<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        DynamicBufferInline<ManagedDetachedBufferElement> loadClearedInline =
            loadCandidateChunk.ReadComponent<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        Assert.Null(clearedInline[0].Value);
        Assert.Null(loadClearedInline[0].Value);
        Assert.Equal("published-stale-inline", publishedInline[0].Value);
        BufferView<ManagedDetachedBufferElement> publishedBuffer =
            published.Buffers.BorrowRead<ManagedDetachedBufferElement>(entity);
        Assert.Equal("published-a", publishedBuffer[0].Value);
        Assert.Equal("published-b", publishedBuffer[1].Value);
        Assert.Equal("published-c", publishedBuffer[2].Value);

        candidateBuffer.Add(new ManagedDetachedBufferElement { Value = "candidate-a" });
        candidateBuffer.Add(new ManagedDetachedBufferElement { Value = "candidate-b" });
        Assert.Equal(0, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(
            "candidate-b",
            candidate.Buffers.BorrowRead<ManagedDetachedBufferElement>(entity)[1].Value);
        DynamicBufferInline<ManagedDetachedBufferElement> regrownInline =
            candidateChunk.ReadComponent<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        Assert.Null(regrownInline[0].Value);
        Assert.Equal("published-b", publishedBuffer[1].Value);

        candidateBuffer.Clear();
        DynamicBufferInline<ManagedDetachedBufferElement> reclearedInline =
            candidateChunk.ReadComponent<DynamicBufferInline<ManagedDetachedBufferElement>>(
                inlineColumn,
                publishedRecord.RowInChunk);
        Assert.Null(reclearedInline[0].Value);
        Assert.Equal("published-stale-inline", publishedInline[0].Value);
    }

    [Fact]
    public void WorldRootFork_BufferCopyDoesNotClearThePublishedTargetOverflow()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        world.AddBuffer<ManagedDetachedBufferElement>(source);
        world.AddBuffer<ManagedDetachedBufferElement>(target);
        world.ExecuteBufferWrite<ManagedDetachedBufferElement>(source, static buffer =>
        {
            buffer.Add(new ManagedDetachedBufferElement { Value = "source-a" });
            buffer.Add(new ManagedDetachedBufferElement { Value = "source-b" });
        });
        world.ExecuteBufferWrite<ManagedDetachedBufferElement>(target, static buffer =>
        {
            buffer.Add(new ManagedDetachedBufferElement { Value = "target-a" });
            buffer.Add(new ManagedDetachedBufferElement { Value = "target-b" });
        });

        WorldStructureRoot published = world.PublishedStructureRoot;
        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);
        Chunk candidateChunk = candidate.Entities.Store.GetRecordReadOnly(target).Chunk!;

        candidate.Buffers.CopyStorage<ManagedDetachedBufferElement>(
            source,
            target,
            added: false);

        Assert.Equal(0, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(
            "source-a",
            candidate.Buffers.BorrowRead<ManagedDetachedBufferElement>(target)[0].Value);
        Assert.Equal(
            "target-a",
            published.Buffers.BorrowRead<ManagedDetachedBufferElement>(target)[0].Value);
        Assert.Equal(
            "target-b",
            published.Buffers.BorrowRead<ManagedDetachedBufferElement>(target)[1].Value);
    }

    [Fact]
    public void WorldRootFork_StructuralMoveKeepsTransferredOverflowLazyAndSwapRemoveSafe()
    {
        var world = new World();
        Entity moved = world.CreateEntity();
        Entity retained = world.CreateEntity();
        world.AddBuffer<DetachedBufferElement>(moved);
        world.AddBuffer<DetachedBufferElement>(retained);
        world.ExecuteBufferWrite<DetachedBufferElement>(moved, static buffer =>
        {
            buffer.Add(new DetachedBufferElement { Value = 10 });
            buffer.Add(new DetachedBufferElement { Value = 11 });
        });
        world.ExecuteBufferWrite<DetachedBufferElement>(retained, static buffer =>
        {
            buffer.Add(new DetachedBufferElement { Value = 20 });
            buffer.Add(new DetachedBufferElement { Value = 21 });
        });

        WorldStructureRoot published = world.PublishedStructureRoot;
        EntityRecord publishedRecord = published.Entities.Store.GetRecordReadOnly(moved);
        int headerColumn = publishedRecord.Archetype!.Column(
            BufferComponents.Header<DetachedBufferElement>());
        object publishedBacking = publishedRecord.Chunk!
            .BufferOverflowBackingIdentity<DetachedBufferElement>(
                headerColumn,
                publishedRecord.RowInChunk)!;

        WorldStructureRoot candidate = published.CloneDetached(world, world.HookStore);
        candidate.Components.Add(moved, new DetachedValue { Value = 7 });
        EntityRecord movedRecord = candidate.Entities.Store.GetRecordReadOnly(moved);
        int movedHeaderColumn = movedRecord.Archetype!.Column(
            BufferComponents.Header<DetachedBufferElement>());
        Chunk movedChunk = movedRecord.Chunk!;

        Assert.Same(
            publishedBacking,
            movedChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                movedHeaderColumn,
                movedRecord.RowInChunk));
        Assert.Equal(0, movedChunk.BufferOverflowDetachCount);
        Assert.Equal(20, candidate.Buffers.BorrowRead<DetachedBufferElement>(retained)[0].Value);

        DynamicBuffer<DetachedBufferElement> writable =
            candidate.Buffers.BorrowWrite<DetachedBufferElement>(moved, writeVersion: 91);
        writable.SwapRemoveAt(0);

        Assert.NotSame(
            publishedBacking,
            movedChunk.BufferOverflowBackingIdentity<DetachedBufferElement>(
                movedHeaderColumn,
                movedRecord.RowInChunk));
        Assert.Equal(1, movedChunk.BufferOverflowDetachCount);
        Assert.Equal(11, candidate.Buffers.BorrowRead<DetachedBufferElement>(moved)[0].Value);
        Assert.Equal(10, published.Buffers.BorrowRead<DetachedBufferElement>(moved)[0].Value);
        Assert.Equal(20, published.Buffers.BorrowRead<DetachedBufferElement>(retained)[0].Value);
    }

    [Fact]
    public void SharedStores_CloneExact_PreservesUnusedValuesMappingsAndCapacity()
    {
        var source = new SharedStores();
        const int sceneComponentId = 37;
        const int faultComponentId = 3;

        var scenes = source.Store<DetachedScene>(sceneComponentId);
        var sourceValues = new[]
        {
            new DetachedScene(10),
            new DetachedScene(20),
            new DetachedScene(30),
            new DetachedScene(40),
            new DetachedScene(50),
        };

        for (int i = 0; i < sourceValues.Length; i++)
            Assert.Equal(i + 1, scenes.GetOrAdd(sourceValues[i]));

        var faultValues = source.Store<FaultingSharedValue>(faultComponentId);
        var retained = new FaultingSharedValue(7, false);
        Assert.Equal(1, faultValues.GetOrAdd(retained));

        int sourceCapacity = source.Capacity;
        int sourceCount = source.Count;
        int valueCapacity = scenes.ValueCapacity;
        int indexCapacity = scenes.IndexCapacity;
        var candidate = source.CloneExact();

        Assert.Equal(sourceCapacity, candidate.Capacity);
        Assert.Equal(sourceCount, candidate.Count);

        var candidateScenes = candidate.Store<DetachedScene>(sceneComponentId);
        Assert.NotSame(scenes, candidateScenes);
        Assert.Same(scenes.BackingIdentity, candidateScenes.BackingIdentity);
        Assert.Equal(0, candidateScenes.DetachCount);
        Assert.Equal(scenes.ValueCount, candidateScenes.ValueCount);
        Assert.Equal(valueCapacity, candidateScenes.ValueCapacity);
        Assert.Equal(indexCapacity, candidateScenes.IndexCapacity);
        Assert.Equal(default, candidateScenes.GetValue(0));
        for (int i = 0; i < sourceValues.Length; i++)
        {
            Assert.True(candidateScenes.TryGetIndex(sourceValues[i], out int candidateIndex));
            Assert.Equal(i + 1, candidateIndex);
            Assert.Equal(sourceValues[i], candidateScenes.GetValue(candidateIndex));
        }

        var candidateOnly = new DetachedScene(60);
        Assert.Equal(sourceValues.Length + 1, candidateScenes.GetOrAdd(candidateOnly));
        Assert.NotSame(scenes.BackingIdentity, candidateScenes.BackingIdentity);
        Assert.Equal(1, candidateScenes.DetachCount);
        Assert.False(scenes.TryGetIndex(candidateOnly, out _));
        Assert.Equal(sourceValues.Length + 1, scenes.ValueCount);

        var candidateFaultValues = candidate.Store<FaultingSharedValue>(faultComponentId);
        Assert.Throws<SharedValueHashException>(() =>
            candidateFaultValues.GetOrAdd(new FaultingSharedValue(8, true)));

        Assert.True(faultValues.TryGetIndex(retained, out int retainedIndex));
        Assert.Equal(1, retainedIndex);
        Assert.Equal(retained, faultValues.GetValue(retainedIndex));

        candidate.Clear();
        Assert.Equal(0, candidate.Count);
        Assert.Equal(sourceCount, source.Count);
        Assert.True(scenes.TryGetIndex(sourceValues[^1], out _));
    }

    [Fact]
    public void QueryRegistry_CloneExact_PreservesHandlesAndRebuildsCandidateMatches()
    {
        int positionId = ComponentMetadata<Position>.Id;
        int velocityId = ComponentMetadata<Velocity>.Id;
        int healthId = ComponentMetadata<Health>.Id;

        var sourceArchetypes = new ArchetypeRegistry();
        var sourcePositionVelocity = sourceArchetypes.GetOrCreate(Sorted(positionId, velocityId));
        var sourcePosition = sourceArchetypes.GetOrCreate([positionId]);

        var readWriteDefinition = new QueryDefinitionBuilder()
            .ReadWrite<Position>()
            .Read<Velocity>()
            .Build();
        var positionDefinition = new QueryDefinitionBuilder()
            .Read<Position>()
            .Build();

        var source = new QueryRegistry();
        QueryHandle readWriteHandle = source.GetOrCreate(
            readWriteDefinition,
            sourceArchetypes.AllArchetypes);
        QueryHandle positionHandle = source.GetOrCreate(
            positionDefinition,
            sourceArchetypes.AllArchetypes);

        var candidateArchetypes = new ArchetypeRegistry();
        var candidatePositionVelocity = candidateArchetypes.GetOrCreate(Sorted(positionId, velocityId));
        var candidatePositionVelocityHealth = candidateArchetypes.GetOrCreate(
            Sorted(positionId, velocityId, healthId));
        var candidatePosition = candidateArchetypes.GetOrCreate([positionId]);

        var candidate = source.CloneExact(candidateArchetypes.AllArchetypes);
        var sourceReadWrite = source.Get(readWriteHandle);
        var candidateReadWrite = candidate.Get(readWriteHandle);

        Assert.Equal(new QueryHandle(0, 1), readWriteHandle);
        Assert.Equal(new QueryHandle(1, 1), positionHandle);
        Assert.Same(readWriteDefinition, candidateReadWrite.Definition);
        Assert.NotSame(sourceReadWrite, candidateReadWrite);
        Assert.NotSame(sourceReadWrite.State, candidateReadWrite.State);
        Assert.Equal(
            new[] { candidatePositionVelocity, candidatePositionVelocityHealth },
            candidateReadWrite.State.Archetypes.ToArray());
        Assert.All(
            candidateReadWrite.State.Matches.ToArray(),
            match => Assert.Contains(
                match.Archetype,
                new[] { candidatePositionVelocity, candidatePositionVelocityHealth }));
        Assert.DoesNotContain(
            candidateReadWrite.State.Matches.ToArray(),
            match => ReferenceEquals(match.Archetype, sourcePositionVelocity));

        var candidatePositionRecord = candidate.Get(positionHandle);
        Assert.Same(positionDefinition, candidatePositionRecord.Definition);
        Assert.Equal(
            new[] { candidatePositionVelocity, candidatePositionVelocityHealth, candidatePosition },
            candidatePositionRecord.State.Archetypes.ToArray());

        Assert.Equal(
            readWriteHandle,
            candidate.GetOrCreate(readWriteDefinition, candidateArchetypes.AllArchetypes));
        Assert.Equal(
            positionHandle,
            candidate.GetOrCreate(positionDefinition, candidateArchetypes.AllArchetypes));
        Assert.Equal(
            new[] { sourcePositionVelocity, sourcePosition },
            source.Get(positionHandle).State.Archetypes.ToArray());
    }

    [Fact]
    public void QueryRegistry_CloneExact_IncrementalMatchAndNewRecordsAreIsolated()
    {
        int positionId = ComponentMetadata<Position>.Id;
        int velocityId = ComponentMetadata<Velocity>.Id;
        int healthId = ComponentMetadata<Health>.Id;

        var definition = new QueryDefinitionBuilder()
            .ReadWrite<Position>()
            .Read<Velocity>()
            .Build();
        var sourceArchetypes = new ArchetypeRegistry();
        var sourceArchetype = sourceArchetypes.GetOrCreate(Sorted(positionId, velocityId));
        var source = new QueryRegistry();
        QueryHandle handle = source.GetOrCreate(definition, sourceArchetypes.AllArchetypes);

        var candidateArchetypes = new ArchetypeRegistry();
        var candidateArchetype = candidateArchetypes.GetOrCreate(Sorted(positionId, velocityId));
        var candidate = source.CloneExact(candidateArchetypes.AllArchetypes);

        var addedCandidate = candidateArchetypes.GetOrCreate(
            Sorted(positionId, velocityId, healthId));
        candidate.OnNewArchetype(addedCandidate);

        Assert.Equal(
            new[] { candidateArchetype, addedCandidate },
            candidate.Get(handle).State.Archetypes.ToArray());
        Assert.Equal(1, source.Get(handle).State.Archetypes.Length);
        Assert.Same(sourceArchetype, source.Get(handle).State.Archetypes[0]);

        var candidateOnlyDefinition = new QueryDefinitionBuilder()
            .Read<Health>()
            .Build();
        QueryHandle candidateOnly = candidate.GetOrCreate(
            candidateOnlyDefinition,
            candidateArchetypes.AllArchetypes);
        Assert.Equal(new QueryHandle(1, 1), candidateOnly);
        Assert.Throws<InvalidOperationException>(() => source.Get(candidateOnly));
    }

    [Fact]
    public void QueryRegistry_ExactClone_ClearsPairMatchCaches()
    {
        int positionId = ComponentMetadata<Position>.Id;
        int velocityId = ComponentMetadata<Velocity>.Id;
        var definition = new QueryDefinitionBuilder()
            .ReadWrite<Position>()
            .Read<Velocity>()
            .Build();

        var sourceArchetypes = new ArchetypeRegistry();
        var sourceArchetype = sourceArchetypes.GetOrCreate(Sorted(positionId, velocityId));
        var sourceRegistry = new QueryRegistry();
        QueryHandle handle = sourceRegistry.GetOrCreate(
            definition,
            sourceArchetypes.AllArchetypes);
        QueryState sourceState = sourceRegistry.Get(handle).State;
        ReadOnlySpan<ReadWriteMatch> sourceAccess =
            sourceState.AccessMatches<Position, Velocity>(
            positionId,
            velocityId);

        ArchetypeRegistry candidateArchetypes = sourceArchetypes.CloneExact(out var tableMap);
        Archetype candidateArchetype = tableMap.Remap(sourceArchetype);
        QueryRegistry candidateRegistry = sourceRegistry.CloneExact(
            tableMap,
            out int clonedMatchCount);

        QueryState candidateState = candidateRegistry.Get(handle).State;
        ReadOnlySpan<ReadWriteMatch> candidateAccess =
            candidateState.AccessMatches<Position, Velocity>(
            positionId,
            velocityId);
        Assert.NotSame(sourceState, candidateState);
        Assert.Equal(1, candidateState.Archetypes.Length);
        Assert.Same(candidateArchetype, candidateState.Archetypes[0]);
        Assert.Equal(1, clonedMatchCount);
        Assert.Equal(1, sourceState.Archetypes.Length);
        Assert.Same(sourceArchetype, sourceState.Archetypes[0]);
        Assert.Same(
            sourceAccess[0].Match,
            sourceState.AccessMatches<Position, Velocity>(positionId, velocityId)[0].Match);
        Assert.NotSame(
            sourceAccess[0].Match,
            candidateAccess[0].Match);
        Assert.Same(sourceArchetype, sourceAccess[0].Archetype);
        Assert.Same(candidateArchetype, candidateAccess[0].Archetype);
    }

    private static int[] Sorted(params int[] ids)
    {
        Array.Sort(ids);
        return ids;
    }

    private readonly record struct DetachedScene(int Value);

    private readonly record struct DetachedBucket(int Value) : ISharedComponent;

    [BufferCapacity(1)]
    private struct DetachedBufferElement : IBufferElement
    {
        public int Value;
    }

    [BufferCapacity(1)]
    private struct ManagedDetachedBufferElement : IBufferElement
    {
        public string? Value;
    }

    private struct DetachedValue : IComponent
    {
        public int Value;
    }

    private readonly record struct FaultingSharedValue(int Value, bool ThrowOnHash)
    {
        public override int GetHashCode()
        {
            if (ThrowOnHash)
                throw new SharedValueHashException();

            return Value;
        }
    }

    private sealed class SharedValueHashException : Exception;
}
