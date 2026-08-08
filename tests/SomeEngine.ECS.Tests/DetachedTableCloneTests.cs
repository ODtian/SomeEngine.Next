using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedTableCloneTests
{
    [Fact]
    public void RegistryClone_OwnsShapesAndRemapsEveryTransitionCache()
    {
        var source = new ArchetypeRegistry();
        var empty = source.GetOrCreate([]);
        var position = source.GetOrCreate([ComponentMetadata<Position>.Id]);
        Archetype positionVelocity = source.AddEdge(position, ComponentMetadata<Velocity>.Id).Target;
        _ = source.RemoveEdge(positionVelocity, ComponentMetadata<Velocity>.Id);

        int[] includedIds =
        [
            ComponentMetadata<Health>.Id,
            ComponentMetadata<PlayerTag>.Id,
        ];
        Array.Sort(includedIds);
        _ = source.IncludeTransition(position, includedIds);

        int[] cleanupIds =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<CleanupMarker>.Id,
        ];
        Array.Sort(cleanupIds);
        Archetype cleanup = source.GetOrCreate(cleanupIds);
        _ = source.CleanupTransition(cleanup);

        int sourceCallbackCount = 0;
        source.OnArchetypeCreated = _ => sourceCallbackCount++;

        ArchetypeRegistry candidate = source.CloneExact(out var map);

        Assert.Equal(0, sourceCallbackCount);
        Assert.Null(candidate.OnArchetypeCreated);
        Assert.Equal(source.AllArchetypes.Length, candidate.AllArchetypes.Length);
        Assert.Equal(source.AllArchetypes.Length, map.ArchetypeCount);

        foreach (Archetype sourceArchetype in source.AllArchetypes)
        {
            Archetype candidateArchetype = map.Remap(sourceArchetype);
            Assert.NotSame(sourceArchetype, candidateArchetype);
            Assert.True(map.IsCandidate(candidateArchetype));
            Assert.Equal(sourceArchetype.ArchetypeId, candidateArchetype.ArchetypeId);
            AssertSpansEqual(sourceArchetype.ComponentIds, candidateArchetype.ComponentIds);
            AssertSpansEqual(sourceArchetype.TableComponentIds, candidateArchetype.TableComponentIds);
            AssertSpansEqual(sourceArchetype.TagIds, candidateArchetype.TagIds);
            Assert.Equal(
                sourceArchetype.ColumnOperations.Length,
                candidateArchetype.ColumnOperations.Length);
            AssertSpansEqual(sourceArchetype.EnableableComponentIds, candidateArchetype.EnableableComponentIds);
            AssertSpansEqual(sourceArchetype.EnableableColumnIndices, candidateArchetype.EnableableColumnIndices);
            AssertSpansEqual(sourceArchetype.SharedComponentIds, candidateArchetype.SharedComponentIds);
            AssertSpansEqual(sourceArchetype.CleanupComponentIds, candidateArchetype.CleanupComponentIds);
            AssertDistinctBacking(sourceArchetype.ComponentIds, candidateArchetype.ComponentIds);
            AssertDistinctBacking(sourceArchetype.TableComponentIds, candidateArchetype.TableComponentIds);
            AssertDistinctBacking(sourceArchetype.TagIds, candidateArchetype.TagIds);
            AssertDistinctBacking(
                sourceArchetype.ColumnOperations,
                candidateArchetype.ColumnOperations);
            AssertDistinctBacking(sourceArchetype.EnableableComponentIds, candidateArchetype.EnableableComponentIds);
            AssertDistinctBacking(sourceArchetype.EnableableColumnIndices, candidateArchetype.EnableableColumnIndices);
            AssertDistinctBacking(sourceArchetype.SharedComponentIds, candidateArchetype.SharedComponentIds);
            AssertDistinctBacking(sourceArchetype.CleanupComponentIds, candidateArchetype.CleanupComponentIds);

            AssertAddTransitionCache(
                sourceArchetype,
                candidateArchetype,
                ComponentMetadata<Velocity>.Id,
                map);
            AssertRemoveTransitionCache(
                sourceArchetype,
                candidateArchetype,
                ComponentMetadata<Velocity>.Id,
                map);
            AssertIncludeCache(sourceArchetype, candidateArchetype, includedIds, map);

            Assert.Equal(sourceArchetype.HasCleanupTransition, candidateArchetype.HasCleanupTransition);
            if (sourceArchetype.HasCleanupTransition)
            {
                Assert.Same(
                    map.Remap(sourceArchetype.CleanupTransition.Target),
                    candidateArchetype.CleanupTransition.Target);
                AssertDistinctBacking(
                    sourceArchetype.CleanupTransition.SharedColumns,
                    candidateArchetype.CleanupTransition.SharedColumns);
                AssertMappingsEqual(
                    sourceArchetype.CleanupTransition.SharedColumns,
                    candidateArchetype.CleanupTransition.SharedColumns);
            }
        }

        var equivalentRegistry = new ArchetypeRegistry();
        Archetype equivalentEmpty = equivalentRegistry.GetOrCreate([]);
        Assert.Throws<InvalidOperationException>(() => map.Remap(equivalentEmpty));

        int expectedNextId = source.AllArchetypes.Length;
        int candidateCallbackCount = 0;
        candidate.OnArchetypeCreated = _ => candidateCallbackCount++;
        Archetype next = candidate.GetOrCreate([ComponentMetadata<EnemyTag>.Id]);
        Assert.Equal(expectedNextId, next.ArchetypeId);
        Assert.Equal(1, candidateCallbackCount);

        Assert.Equal(ComponentMetadata<Position>.Id, position.ComponentIds[0]);
        Assert.Equal(0, empty.ComponentIds.Length);
    }

    [Fact]
    public void SerializationRegistryClone_StartsDerivedTransitionsColdAndRebuildsAgainstLocalImage()
    {
        var source = new ArchetypeRegistry();
        Archetype position = source.GetOrCreate([ComponentMetadata<Position>.Id]);
        Archetype positionVelocity = source.AddEdge(
            position,
            ComponentMetadata<Velocity>.Id).Target;
        _ = source.RemoveEdge(positionVelocity, ComponentMetadata<Velocity>.Id);

        ArchetypeRegistry candidate = source.CloneExact(
            out DetachedTableMap map,
            cloneDerivedCaches: false);
        Archetype candidatePosition = map.Remap(position);
        Archetype candidatePositionVelocity = map.Remap(positionVelocity);

        Assert.Equal(0, candidatePosition.AddTransitionCount);
        Assert.Equal(0, candidatePosition.RemoveTransitionCount);
        Assert.Equal(0, candidatePosition.IncludeTransitionCount);
        Assert.False(candidatePosition.HasCleanupTransition);

        StructuralTransition rebuilt = candidate.AddEdge(
            candidatePosition,
            ComponentMetadata<Velocity>.Id);

        Assert.Same(candidatePositionVelocity, rebuilt.Target);
        Assert.True(
            candidatePosition.TryGetAddTransition(
                ComponentMetadata<Velocity>.Id,
                out StructuralTransition candidateCached));
        Assert.Equal(rebuilt, candidateCached);
        Assert.True(
            position.TryGetAddTransition(
                ComponentMetadata<Velocity>.Id,
                out StructuralTransition sourceCached));
        Assert.NotSame(sourceCached.Target, rebuilt.Target);
        AssertDistinctBacking(sourceCached.SharedColumns, rebuilt.SharedColumns);
    }

    [Fact]
    public void ChunkFork_SharesBackingUntilFirstWriteThenDetachesExactlyOnce()
    {
        var source = new ArchetypeRegistry();
        int[] ids =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<VisibilityState>.Id,
        ];
        Array.Sort(ids);
        Archetype sourceArchetype = source.GetOrCreate(ids);
        var sourceChunk = new Chunk(
            capacity: 4,
            sourceArchetype.ColumnOperations,
            sourceArchetype.EnableableComponentIds.Length)
        {
            Count = 2,
            IndexInArchetype = 3,
            OrderVersion = 91,
        };
        sourceArchetype.AddChunk(sourceChunk);

        int positionColumn = sourceArchetype.Column(ComponentMetadata<Position>.Id);
        int visibilityColumn = sourceArchetype.Column(ComponentMetadata<VisibilityState>.Id);
        for (int row = 0; row < sourceChunk.Capacity; row++)
        {
            sourceChunk.Entities[row] = new Entity(row + 1, row + 10);
            sourceChunk.WriteComponent(positionColumn, row, new Position { X = row + 0.25f, Y = row + 1.5f });
            sourceChunk.WriteComponent(visibilityColumn, row, new VisibilityState { Value = row + 20 });
            sourceChunk.AddVersionRows(positionColumn)[row] = (uint)(100 + row);
            sourceChunk.WriteVersionRows(positionColumn)[row] = (uint)(200 + row);
            sourceChunk.AddVersionRows(visibilityColumn)[row] = (uint)(300 + row);
            sourceChunk.WriteVersionRows(visibilityColumn)[row] = (uint)(400 + row);
        }

        sourceChunk.ChangeVersions[positionColumn] = 501;
        sourceChunk.ChangeVersions[visibilityColumn] = 502;
        sourceChunk.WriteEnabled(0, 0, true);
        sourceChunk.WriteEnabled(0, 3, true);

        ArchetypeRegistry candidate = source.CloneExact(out var map);
        _ = candidate;
        Archetype candidateArchetype = map.Remap(sourceArchetype);
        Chunk candidateChunk = map.Remap(sourceChunk);

        Assert.Same(candidateChunk, candidateArchetype.Chunks[0]);
        Assert.True(map.IsCandidate(candidateChunk));
        Assert.Equal(sourceChunk.Count, candidateChunk.Count);
        Assert.Equal(sourceChunk.Capacity, candidateChunk.Capacity);
        Assert.Equal(sourceChunk.IndexInArchetype, candidateChunk.IndexInArchetype);
        Assert.Equal(sourceChunk.OrderVersion, candidateChunk.OrderVersion);
        long sharedStorageIdentity = sourceChunk.StorageIdentity;
        Assert.True(candidateChunk.SharesStorageWith(sourceChunk));
        Assert.Equal(sharedStorageIdentity, candidateChunk.StorageIdentity);
        Assert.False(candidateChunk.OwnsStorage);
        AssertSameBacking(sourceChunk.Entities, candidateChunk.Entities);
        AssertSameBacking(sourceChunk.ChangeVersions, candidateChunk.ChangeVersions);
        AssertSameBacking(sourceChunk.EnableMasks, candidateChunk.EnableMasks);
        AssertSpansEqual(sourceChunk.Entities, candidateChunk.Entities);
        AssertSpansEqual(sourceChunk.ChangeVersions, candidateChunk.ChangeVersions);
        AssertSpansEqual(sourceChunk.EnableMasks, candidateChunk.EnableMasks);

        for (int column = 0; column < sourceChunk.ColumnCount; column++)
        {
            Assert.True(sourceChunk.SharesColumnBackingWith(candidateChunk, column));
            AssertSameBacking(
                sourceChunk.AddVersionRows(column),
                candidateChunk.AddVersionRows(column));
            AssertSameBacking(
                sourceChunk.WriteVersionRows(column),
                candidateChunk.WriteVersionRows(column));
            AssertSpansEqual(
                sourceChunk.AddVersionRows(column),
                candidateChunk.AddVersionRows(column));
            AssertSpansEqual(
                sourceChunk.WriteVersionRows(column),
                candidateChunk.WriteVersionRows(column));
        }

        Assert.Equal(
            sourceChunk.ReadComponent<Position>(positionColumn, 3).X,
            candidateChunk.ReadComponent<Position>(positionColumn, 3).X);
        candidateChunk.WriteComponent(positionColumn, 3, new Position { X = 999, Y = 1000 });
        long detachedStorageIdentity = candidateChunk.StorageIdentity;
        Assert.False(candidateChunk.SharesStorageWith(sourceChunk));
        Assert.NotEqual(sharedStorageIdentity, detachedStorageIdentity);
        Assert.Equal(sourceChunk.StorageVersion + 1, candidateChunk.StorageVersion);
        Assert.True(candidateChunk.OwnsStorage);

        candidateChunk.Entities[3] = Entity.Null;
        candidateChunk.AddVersionRows(positionColumn)[3] = 999;
        candidateChunk.WriteEnabled(0, 3, false);

        Assert.Equal(detachedStorageIdentity, candidateChunk.StorageIdentity);

        Assert.Equal(3.25f, sourceChunk.ReadComponent<Position>(positionColumn, 3).X);
        Assert.NotEqual(Entity.Null, sourceChunk.Entities[3]);
        Assert.Equal(103u, sourceChunk.AddVersionRows(positionColumn)[3]);
        Assert.True(sourceChunk.IsEnabled(0, 3));
    }

    [Fact]
    public void ChunkFork_EqualEnableBitWriteStaysSharedUntilBitChanges()
    {
        var registry = new ArchetypeRegistry();
        Archetype sourceArchetype = registry.GetOrCreate(
            [ComponentMetadata<VisibilityState>.Id]);
        var sourceChunk = new Chunk(
            capacity: 1,
            sourceArchetype.ColumnOperations,
            sourceArchetype.EnableableComponentIds.Length)
        {
            Count = 1,
        };
        sourceArchetype.AddChunk(sourceChunk);
        sourceChunk.WriteEnabled(maskIndex: 0, row: 0, enabled: true);

        _ = registry.CloneExact(out var map);
        Chunk candidateChunk = map.Remap(sourceChunk);
        long sharedStorageIdentity = sourceChunk.StorageIdentity;

        candidateChunk.WriteEnabled(maskIndex: 0, row: 0, enabled: true);

        Assert.True(sourceChunk.SharesStorageWith(candidateChunk));
        Assert.Equal(sharedStorageIdentity, candidateChunk.StorageIdentity);
        Assert.True(candidateChunk.IsEnabled(0, 0));

        candidateChunk.WriteEnabled(maskIndex: 0, row: 0, enabled: false);

        Assert.False(sourceChunk.SharesStorageWith(candidateChunk));
        Assert.NotEqual(sharedStorageIdentity, candidateChunk.StorageIdentity);
        Assert.True(sourceChunk.IsEnabled(0, 0));
        Assert.False(candidateChunk.IsEnabled(0, 0));
    }

    [Fact]
    public async Task ChunkFork_ConcurrentFirstWritersShareOneDetachedBackingWithoutLostRows()
    {
        const int writerCount = 16;
        var registry = new ArchetypeRegistry();
        Archetype archetype = registry.GetOrCreate([ComponentMetadata<Position>.Id]);
        int column = archetype.Column(ComponentMetadata<Position>.Id);
        var source = new Chunk(writerCount, archetype.ColumnOperations)
        {
            Count = writerCount,
        };
        archetype.AddChunk(source);
        for (int row = 0; row < writerCount; row++)
            source.WriteComponent(column, row, new Position { X = -1, Y = -1 });

        _ = registry.CloneExact(out var map);
        Chunk candidate = map.Remap(source);
        Assert.True(source.SharesStorageWith(candidate));

        using var begin = new Barrier(writerCount);
        using var referencesCaptured = new Barrier(writerCount);
        var tasks = new Task[writerCount];
        for (int row = 0; row < writerCount; row++)
        {
            int capturedRow = row;
            tasks[row] = Task.Run(() =>
            {
                begin.SignalAndWait();
                ref Position value = ref candidate.GetComponentRef<Position>(column, capturedRow);
                referencesCaptured.SignalAndWait();
                value = new Position { X = capturedRow, Y = capturedRow + 1000 };
                candidate.MarkWrite(column, capturedRow, version: 77);
            });
        }

        await Task.WhenAll(tasks);

        Assert.False(source.SharesStorageWith(candidate));
        Assert.Equal(source.StorageVersion + 1, candidate.StorageVersion);
        for (int row = 0; row < writerCount; row++)
        {
            Position value = candidate.ReadComponent<Position>(column, row);
            Assert.Equal((float)row, value.X);
            Assert.Equal((float)(row + 1000), value.Y);
            Assert.Equal(77u, candidate.WriteVersionRows(column)[row]);

            Position published = source.ReadComponent<Position>(column, row);
            Assert.Equal(-1f, published.X);
            Assert.Equal(-1f, published.Y);
        }
    }

    [Fact]
    public void RegistryClone_PreservesSharedBucketOrderWithCandidateOnlyChunks()
    {
        var source = new ArchetypeRegistry();
        Archetype sourceArchetype = source.GetOrCreate([ComponentMetadata<SceneId>.Id]);
        var sharedTuple = new SharedComponentTuple([7]);
        var first = new Chunk(
            2,
            sourceArchetype.ColumnOperations,
            sharedValues: sharedTuple)
        {
            IndexInArchetype = 0,
        };
        var second = new Chunk(
            4,
            sourceArchetype.ColumnOperations,
            sharedValues: sharedTuple)
        {
            IndexInArchetype = 1,
        };
        sourceArchetype.AddChunk(first);
        sourceArchetype.AddChunk(second);

        SharedChunkBucket sourceBucket =
            sourceArchetype.GetOrAddSharedChunkBucket(sharedTuple);
        sourceBucket.Register(second);
        sourceBucket.Register(first);

        _ = source.CloneExact(out var map);
        Archetype candidateArchetype = map.Remap(sourceArchetype);
        sourceBucket = sourceArchetype.GetOnlySharedChunkBucket();
        SharedChunkBucket candidateBucket = candidateArchetype.GetOnlySharedChunkBucket();

        Assert.Same(sourceBucket.Values, candidateBucket.Values);
        Assert.Equal(
            sourceBucket.Values.AsSpan().ToArray(),
            candidateBucket.Values.AsSpan().ToArray());
        Assert.NotSame(sourceBucket, candidateBucket);
        Assert.Equal(2, candidateBucket.OpenChunkCount);
        Assert.Same(map.Remap(second), candidateBucket.OpenChunkAt(0));
        Assert.Same(map.Remap(first), candidateBucket.OpenChunkAt(1));
        Assert.True(map.IsCandidate(candidateBucket.OpenChunkAt(0)));
        Assert.True(map.IsCandidate(candidateBucket.OpenChunkAt(1)));
        Assert.Same(first.SharedValues, map.Remap(first).SharedValues);
        Assert.Same(second.SharedValues, map.Remap(second).SharedValues);

        map.Remap(first).EnsureWritable();
        Chunk candidateSecond = map.Remap(second);
        candidateSecond.Count = candidateSecond.Capacity;
        candidateBucket.MarkFull(candidateSecond);
        Assert.Same(first.SharedValues, map.Remap(first).SharedValues);
        Assert.Equal(7, first.SharedValues![0]);
        Assert.Equal(7, map.Remap(first).SharedValues![0]);
        Assert.Equal(2, sourceBucket.OpenChunkCount);
        Assert.Equal(1, candidateBucket.OpenChunkCount);
    }

    [Fact]
    public void SharedAllocation_UsesOneImmutableTuplePerBucketAndReusesItAcrossForks()
    {
        var sourceEntities = new SomeEngine.ECS.Owners.Entities(capacity: 1);
        var sourceTables = new SomeEngine.ECS.Owners.Tables(sourceEntities, static _ => { });
        Archetype sourceArchetype = sourceTables.Registry.GetOrCreate(
            [ComponentMetadata<SceneId>.Id]);

        int[] requestedValues = [7];
        Entity firstEntity = sourceEntities.Store.Allocate();
        (Chunk firstChunk, int firstRow) = sourceTables.AllocateShared(
            sourceArchetype,
            firstEntity,
            requestedValues);
        Place(sourceEntities.Store, firstEntity, sourceArchetype, firstChunk, firstRow);

        requestedValues[0] = 99;
        Assert.Equal(7, firstChunk.SharedValues![0]);

        for (int index = 1; index <= firstChunk.Capacity; index++)
        {
            Entity entity = sourceEntities.Store.Allocate();
            (Chunk chunk, int row) = sourceTables.AllocateShared(
                sourceArchetype,
                entity,
                [7]);
            Place(sourceEntities.Store, entity, sourceArchetype, chunk, row);
        }

        Assert.Equal(2, sourceArchetype.Chunks.Length);
        SharedChunkBucket sourceBucket = sourceArchetype.GetOnlySharedChunkBucket();
        Assert.Equal(2, sourceBucket.ChunkCount);
        Assert.Equal(1, sourceBucket.OpenChunkCount);
        Assert.Equal([7], sourceBucket.Values.AsSpan().ToArray());
        foreach (Chunk chunk in sourceArchetype.Chunks)
            Assert.Same(sourceBucket.Values, chunk.SharedValues);

        ArchetypeRegistry candidateRegistry = sourceTables.Registry.CloneExact(out var map);
        Archetype candidateArchetype = map.Remap(sourceArchetype);
        SharedChunkBucket candidateBucket = candidateArchetype.GetOnlySharedChunkBucket();

        Assert.Same(sourceBucket.Values, candidateBucket.Values);
        Assert.NotSame(sourceBucket, candidateBucket);
        Assert.Equal(sourceBucket.ChunkCount, candidateBucket.ChunkCount);
        Assert.Equal(sourceBucket.LastCapacity, candidateBucket.LastCapacity);
        Assert.Same(
            map.Remap(sourceBucket.OpenChunkAt(0)),
            candidateBucket.OpenChunkAt(0));
        foreach (Chunk chunk in candidateArchetype.Chunks)
            Assert.Same(candidateBucket.Values, chunk.SharedValues);

        _ = candidateRegistry;
    }

    [Fact]
    public void RegistryFork_DetachesOnlyTheExplicitlyWrittenBufferOverflowRow()
    {
        int headerId = BufferComponents.Header<IntElement>();
        int inlineId = BufferComponents.Inline<IntElement>();
        int[] ids = [headerId, inlineId];
        Array.Sort(ids);

        var source = new ArchetypeRegistry();
        Archetype sourceArchetype = source.GetOrCreate(ids);
        var sourceChunk = new Chunk(4, sourceArchetype.ColumnOperations)
        {
            Count = 3,
        };
        sourceArchetype.AddChunk(sourceChunk);
        int headerColumn = sourceArchetype.Column(headerId);
        int inlineColumn = sourceArchetype.Column(inlineId);

        ref DynamicBufferHeader<IntElement> inlineHeader =
            ref sourceChunk.GetComponentRef<DynamicBufferHeader<IntElement>>(headerColumn, 0);
        inlineHeader = DynamicBufferHeader<IntElement>.Create();
        inlineHeader.Count = 2;
        ref DynamicBufferInline<IntElement> inlineStorage =
            ref sourceChunk.GetComponentRef<DynamicBufferInline<IntElement>>(inlineColumn, 0);
        inlineStorage[0] = new IntElement { Value = 11 };
        inlineStorage[1] = new IntElement { Value = 12 };

        ref DynamicBufferHeader<IntElement> overflowHeader =
            ref sourceChunk.GetComponentRef<DynamicBufferHeader<IntElement>>(headerColumn, 1);
        overflowHeader = DynamicBufferHeader<IntElement>.Create();
        overflowHeader.Count = 10;
        sourceChunk.SetOwnedBufferOverflow(ref overflowHeader, new IntElement[16]);
        for (int i = 0; i < overflowHeader.Count; i++)
            overflowHeader.OverflowWriteSpan[i] = new IntElement { Value = 100 + i };
        overflowHeader.OverflowWriteSpan[15] = new IntElement { Value = 1515 };

        ref DynamicBufferHeader<IntElement> retainedCapacityHeader =
            ref sourceChunk.GetComponentRef<DynamicBufferHeader<IntElement>>(headerColumn, 2);
        retainedCapacityHeader = DynamicBufferHeader<IntElement>.Create();
        retainedCapacityHeader.Count = 0;
        sourceChunk.SetOwnedBufferOverflow(
            ref retainedCapacityHeader,
            new IntElement[4096]);
        retainedCapacityHeader.OverflowWriteSpan[4095] =
            new IntElement { Value = 3131 };

        _ = source.CloneExact(out var map);
        Chunk candidateChunk = map.Remap(sourceChunk);
        var candidateInlineHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 0);
        var candidateOverflowHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 1);
        var candidateRetainedCapacityHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 2);

        Assert.True(sourceChunk.SharesStorageWith(candidateChunk));
        Assert.False(candidateInlineHeader.HasOverflow);
        Assert.Equal(2, candidateInlineHeader.Count);
        Assert.Equal(10, candidateOverflowHeader.Count);
        Assert.True(candidateOverflowHeader.HasOverflow);
        Assert.Same(
            overflowHeader.OverflowBackingIdentity,
            candidateOverflowHeader.OverflowBackingIdentity);
        Assert.Equal(
            overflowHeader.OverflowCapacity,
            candidateOverflowHeader.OverflowCapacity);
        Assert.Equal(109, candidateOverflowHeader.OverflowReadSpan[9].Value);
        Assert.Equal(0, candidateRetainedCapacityHeader.Count);
        Assert.True(candidateRetainedCapacityHeader.HasOverflow);
        Assert.Same(
            retainedCapacityHeader.OverflowBackingIdentity,
            candidateRetainedCapacityHeader.OverflowBackingIdentity);
        Assert.Equal(4096, candidateRetainedCapacityHeader.OverflowCapacity);
        Assert.Equal(3131, candidateRetainedCapacityHeader.OverflowReadSpan[4095].Value);

        ref DynamicBufferInline<IntElement> candidateInline =
            ref candidateChunk.GetComponentRef<DynamicBufferInline<IntElement>>(inlineColumn, 0);
        Assert.False(sourceChunk.SharesStorageWith(candidateChunk));
        candidateOverflowHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 1);
        candidateRetainedCapacityHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 2);
        Assert.Same(
            overflowHeader.OverflowBackingIdentity,
            candidateOverflowHeader.OverflowBackingIdentity);
        Assert.Same(
            retainedCapacityHeader.OverflowBackingIdentity,
            candidateRetainedCapacityHeader.OverflowBackingIdentity);
        Assert.Equal(0, candidateChunk.BufferOverflowDetachCount);

        ref DynamicBufferHeader<IntElement> writableOverflowHeader =
            ref candidateChunk.GetBufferHeaderWithWritableOverflow<IntElement>(headerColumn, 1);
        Assert.Equal(16, writableOverflowHeader.OverflowCapacity);
        Assert.Equal(109, writableOverflowHeader.OverflowReadSpan[9].Value);
        Assert.Equal(0, writableOverflowHeader.OverflowReadSpan[15].Value);
        writableOverflowHeader.OverflowWriteSpan[0] = new IntElement { Value = 999 };
        candidateInline[0] = new IntElement { Value = 999 };

        candidateOverflowHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 1);
        candidateRetainedCapacityHeader =
            candidateChunk.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 2);
        Assert.NotSame(
            overflowHeader.OverflowBackingIdentity,
            candidateOverflowHeader.OverflowBackingIdentity);
        Assert.Same(
            retainedCapacityHeader.OverflowBackingIdentity,
            candidateRetainedCapacityHeader.OverflowBackingIdentity);
        Assert.Equal(1, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(100, overflowHeader.OverflowReadSpan[0].Value);
        Assert.Equal(1515, overflowHeader.OverflowReadSpan[15].Value);
        Assert.Equal(3131, retainedCapacityHeader.OverflowReadSpan[4095].Value);
        Assert.Equal(11, inlineStorage[0].Value);

        _ = candidateChunk.GetBufferHeaderWithWritableOverflow<IntElement>(headerColumn, 1);
        Assert.Equal(1, candidateChunk.BufferOverflowDetachCount);

        ref DynamicBufferHeader<IntElement> writableRetainedCapacity =
            ref candidateChunk.GetBufferHeaderWithWritableOverflow<IntElement>(headerColumn, 2);
        Assert.Equal(4096, writableRetainedCapacity.OverflowCapacity);
        Assert.Equal(0, writableRetainedCapacity.OverflowReadSpan[4095].Value);
        writableRetainedCapacity.OverflowWriteSpan[4095] =
            new IntElement { Value = 999 };
        Assert.Equal(2, candidateChunk.BufferOverflowDetachCount);
        Assert.Equal(3131, retainedCapacityHeader.OverflowReadSpan[4095].Value);
    }

    [Fact]
    public void CandidateChunkPromotion_SharesOverflowUntilThePromotedRowIsWritten()
    {
        int headerId = BufferComponents.Header<IntElement>();
        int inlineId = BufferComponents.Inline<IntElement>();
        int[] ids = [headerId, inlineId];
        Array.Sort(ids);

        var sourceEntities = new SomeEngine.ECS.Owners.Entities(capacity: 1);
        var sourceTables = new SomeEngine.ECS.Owners.Tables(sourceEntities, static _ => { });
        Archetype sourceArchetype = sourceTables.Registry.GetOrCreate(ids);
        Assert.True(sourceArchetype.InitialChunkRows < sourceArchetype.MaxChunkRows);

        Chunk? sourceChunk = null;
        for (int row = 0; row < sourceArchetype.InitialChunkRows; row++)
        {
            Entity entity = sourceEntities.Store.Allocate();
            (Chunk chunk, int allocatedRow) = sourceTables.AllocateInChunk(sourceArchetype, entity);
            sourceChunk ??= chunk;
            Assert.Same(sourceChunk, chunk);
            Place(sourceEntities.Store, entity, sourceArchetype, chunk, allocatedRow);
        }

        int headerColumn = sourceArchetype.Column(headerId);
        ref DynamicBufferHeader<IntElement> sourceHeader =
            ref sourceChunk!.GetComponentRef<DynamicBufferHeader<IntElement>>(headerColumn, 0);
        sourceHeader = DynamicBufferHeader<IntElement>.Create();
        sourceHeader.Count = 10;
        sourceChunk.SetOwnedBufferOverflow(ref sourceHeader, new IntElement[16]);
        sourceHeader.OverflowWriteSpan[0] = new IntElement { Value = 41 };
        object publishedOverflow = sourceHeader.OverflowBackingIdentity!;

        ArchetypeRegistry candidateRegistry = sourceTables.Registry.CloneExact(out var map);
        EntityStore candidateStore = sourceEntities.Store.CloneExact(candidateRegistry);
        var candidateEntities = new SomeEngine.ECS.Owners.Entities(candidateStore);
        var candidateTables = new SomeEngine.ECS.Owners.Tables(
            candidateEntities,
            candidateRegistry,
            static _ => { });
        Archetype candidateArchetype = map.Remap(sourceArchetype);
        Chunk forkedChunk = map.Remap(sourceChunk);
        Assert.True(forkedChunk.SharesStorageWith(sourceChunk));

        Entity appended = candidateStore.Allocate();
        (Chunk promoted, int appendedRow) = candidateTables.AllocateInChunk(
            candidateArchetype,
            appended);
        Place(candidateStore, appended, candidateArchetype, promoted, appendedRow);

        Assert.Equal(1, candidateArchetype.Chunks.Length);
        Assert.Same(promoted, candidateArchetype.Chunks[0]);
        Assert.NotSame(forkedChunk, promoted);
        Assert.True(promoted.Capacity > forkedChunk.Capacity);
        candidateStore.ValidateTableResolver(candidateRegistry);
        DynamicBufferHeader<IntElement> promotedHeader =
            promoted.ReadComponent<DynamicBufferHeader<IntElement>>(headerColumn, 0);
        Assert.True(promotedHeader.HasOverflow);
        Assert.Same(publishedOverflow, promotedHeader.OverflowBackingIdentity);
        Assert.Equal(0, promoted.BufferOverflowDetachCount);

        ref DynamicBufferHeader<IntElement> writablePromotedHeader =
            ref promoted.GetBufferHeaderWithWritableOverflow<IntElement>(headerColumn, 0);
        writablePromotedHeader.OverflowWriteSpan[0] = new IntElement { Value = 99 };
        Assert.NotSame(publishedOverflow, writablePromotedHeader.OverflowBackingIdentity);
        Assert.Equal(1, promoted.BufferOverflowDetachCount);
        Assert.Equal(41, sourceHeader.OverflowReadSpan[0].Value);
        Assert.Equal(99, writablePromotedHeader.OverflowReadSpan[0].Value);
    }

    [Fact]
    public void DetachedTableResolver_RecycleDropsRetiredChunkAndTracksSwapReplacement()
    {
        var sourceEntities = new SomeEngine.ECS.Owners.Entities(capacity: 1);
        var sourceTables = new SomeEngine.ECS.Owners.Tables(sourceEntities, static _ => { });
        Archetype sourceArchetype = sourceTables.Registry.GetOrCreate(
            [ComponentMetadata<Position>.Id]);
        sourceTables.EnsureCapacity(
            sourceArchetype,
            sourceArchetype.MaxChunkRows + 1);
        Assert.Equal(2, sourceArchetype.Chunks.Length);
        sourceEntities.Store.ValidateTableResolver(sourceTables.Registry);

        ArchetypeRegistry candidateRegistry = sourceTables.Registry.CloneExact(out var map);
        EntityStore candidateStore = sourceEntities.Store.CloneExact(candidateRegistry);
        var candidateEntities = new SomeEngine.ECS.Owners.Entities(candidateStore);
        var candidateTables = new SomeEngine.ECS.Owners.Tables(
            candidateEntities,
            candidateRegistry,
            static _ => { });
        Archetype candidateArchetype = map.Remap(sourceArchetype);
        Chunk retired = candidateArchetype.Chunks[0];
        Chunk moved = candidateArchetype.Chunks[1];

        candidateTables.TryRecycleChunk(candidateArchetype, retired);

        Assert.Equal(1, candidateArchetype.Chunks.Length);
        Assert.Same(moved, candidateArchetype.Chunks[0]);
        Assert.Equal(0, moved.IndexInArchetype);
        candidateStore.ValidateTableResolver(candidateRegistry);
    }

    [Fact]
    public void DetachedTableResolver_TracksCandidateOnlyArchetypeAndFirstChunk()
    {
        var sourceEntities = new SomeEngine.ECS.Owners.Entities(capacity: 1);
        var sourceTables = new SomeEngine.ECS.Owners.Tables(sourceEntities, static _ => { });
        ArchetypeRegistry candidateRegistry = sourceTables.Registry.CloneExact(out var map);
        EntityStore candidateStore = sourceEntities.Store.CloneExact(candidateRegistry);
        var candidateEntities = new SomeEngine.ECS.Owners.Entities(candidateStore);
        var candidateTables = new SomeEngine.ECS.Owners.Tables(
            candidateEntities,
            candidateRegistry,
            static _ => { });

        Archetype candidateOnly = candidateRegistry.GetOrCreate(
            [ComponentMetadata<Position>.Id]);
        Entity entity = candidateStore.Allocate();
        (Chunk chunk, int row) = candidateTables.AllocateInChunk(candidateOnly, entity);
        Place(candidateStore, entity, candidateOnly, chunk, row);

        EntityRecord resolved = candidateStore.GetRecordReadOnly(entity);
        Assert.Same(candidateOnly, resolved.Archetype);
        Assert.Same(chunk, resolved.Chunk);
        candidateStore.ValidateTableResolver(candidateRegistry);
    }

    [Fact]
    public void EntityStoreClone_PreservesAllocatorSequenceAndResolvesLiveRecordsInCandidateRoot()
    {
        var sourceRegistry = new ArchetypeRegistry();
        Archetype sourceArchetype = sourceRegistry.GetOrCreate([ComponentMetadata<Position>.Id]);
        var sourceChunk = new Chunk(4, sourceArchetype.ColumnOperations)
        {
            Count = 2,
            IndexInArchetype = 0,
        };
        sourceArchetype.AddChunk(sourceChunk);

        var sourceStore = new EntityStore(initialCapacity: 9);
        sourceStore.InstallTableImage(sourceRegistry);
        Entity first = sourceStore.Allocate();
        Entity second = sourceStore.Allocate();
        Entity third = sourceStore.Allocate();
        Entity fourth = sourceStore.Allocate();

        sourceStore.Free(second);
        sourceStore.Free(third);

        Place(sourceStore, first, sourceArchetype, sourceChunk, row: 0);
        Place(sourceStore, fourth, sourceArchetype, sourceChunk, row: 1);
        EntityRecordWriter firstRecord = sourceStore.GetRecord(first);
        firstRecord.FreeListNext = 812;
        firstRecord.PendingDestroy = true;
        EntityRecordWriter fourthRecord = sourceStore.GetRecord(fourth);
        fourthRecord.FreeListNext = 913;

        ArchetypeRegistry candidateRegistry = sourceRegistry.CloneExact(out var map);
        sourceStore.ValidateExact(map);
        EntityStore candidateStore = sourceStore.CloneExact(candidateRegistry);
        Assert.True(sourceStore.SharesRecordPageWith(candidateStore, first.Index));
        long sharedPageIdentity = sourceStore.RecordPageIdentity(first.Index);
        long sharedPageVersion = sourceStore.RecordPageVersion(first.Index);
        _ = candidateStore.GetRecord(first);
        Assert.False(sourceStore.SharesRecordPageWith(candidateStore, first.Index));
        Assert.NotEqual(sharedPageIdentity, candidateStore.RecordPageIdentity(first.Index));
        Assert.Equal(sharedPageVersion + 1, candidateStore.RecordPageVersion(first.Index));
        Assert.True(candidateStore.OwnsRecordPage(first.Index));

        Assert.Equal(sourceStore.Count, candidateStore.Count);
        Assert.Equal(sourceStore.AliveCount, candidateStore.AliveCount);
        for (int index = 0; index <= sourceStore.Count; index++)
        {
            EntityRecord sourceRecord = sourceStore.RecordSnapshot(index);
            EntityRecord candidateRecord = candidateStore.RecordSnapshot(index);
            Assert.Equal(sourceRecord.FreeListNext, candidateRecord.FreeListNext);
            Assert.Equal(sourceRecord.RowInChunk, candidateRecord.RowInChunk);
            Assert.Equal(sourceRecord.Generation, candidateRecord.Generation);
            Assert.Equal(sourceRecord.PendingDestroy, candidateRecord.PendingDestroy);

            if (sourceRecord.Archetype is null)
            {
                Assert.Null(candidateRecord.Archetype);
                Assert.Null(candidateRecord.Chunk);
            }
            else
            {
                Assert.Same(map.Remap(sourceRecord.Archetype!), candidateRecord.Archetype);
                Assert.Same(map.Remap(sourceRecord.Chunk!), candidateRecord.Chunk);
                Assert.NotSame(sourceRecord.Archetype, candidateRecord.Archetype);
                Assert.NotSame(sourceRecord.Chunk, candidateRecord.Chunk);
            }
        }

        candidateStore.GetRecord(first).FreeListNext = 999;
        Assert.Equal(812, sourceStore.RecordSnapshot(first.Index).FreeListNext);

        Assert.Equal(sourceStore.Allocate(), candidateStore.Allocate());
        Assert.Equal(sourceStore.Allocate(), candidateStore.Allocate());
        Assert.Equal(sourceStore.Allocate(), candidateStore.Allocate());

        firstRecord.RowInChunk = sourceChunk.Count;
        Assert.Throws<InvalidOperationException>(() => sourceStore.ValidateExact(map));
    }

    [Fact]
    public async Task EntityStoreFork_ConcurrentFirstWritersShareOneDetachedRecordPage()
    {
        const int writerCount = 16;
        var registry = new ArchetypeRegistry();
        Archetype archetype = registry.GetOrCreate([ComponentMetadata<Position>.Id]);
        var chunk = new Chunk(writerCount, archetype.ColumnOperations)
        {
            Count = writerCount,
        };
        archetype.AddChunk(chunk);
        var source = new EntityStore(writerCount);
        source.InstallTableImage(registry);
        var entities = new Entity[writerCount];
        for (int row = 0; row < writerCount; row++)
        {
            Entity entity = source.Allocate();
            entities[row] = entity;
            Place(source, entity, archetype, chunk, row);
        }

        ArchetypeRegistry candidateRegistry = registry.CloneExact(out _);
        EntityStore candidate = source.CloneExact(candidateRegistry);
        Assert.True(source.SharesRecordPageWith(candidate, entities[0].Index));

        using var begin = new Barrier(writerCount);
        using var referencesCaptured = new Barrier(writerCount);
        var tasks = new Task[writerCount];
        for (int index = 0; index < writerCount; index++)
        {
            int capturedIndex = index;
            tasks[index] = Task.Run(() =>
            {
                begin.SignalAndWait();
                EntityRecordWriter record = candidate.GetRecord(entities[capturedIndex]);
                referencesCaptured.SignalAndWait();
                record.FreeListNext = capturedIndex + 1;
            });
        }

        await Task.WhenAll(tasks);

        Assert.False(source.SharesRecordPageWith(candidate, entities[0].Index));
        Assert.Equal(source.RecordPageVersion(entities[0].Index) + 1,
            candidate.RecordPageVersion(entities[0].Index));
        for (int index = 0; index < writerCount; index++)
        {
            Assert.Equal(index + 1, candidate.RecordSnapshot(entities[index].Index).FreeListNext);
            Assert.Equal(0, source.RecordSnapshot(entities[index].Index).FreeListNext);
        }
    }

    [Fact]
    public void EntityStoreFork_DetachesOnlyTouchedPageAndComposesRootLocalResolution()
    {
        const int entityCount = 520;
        var sourceRegistry = new ArchetypeRegistry();
        Archetype sourceArchetype = sourceRegistry.GetOrCreate([ComponentMetadata<Position>.Id]);
        var sourceChunk = new Chunk(entityCount, sourceArchetype.ColumnOperations)
        {
            Count = entityCount,
            IndexInArchetype = 0,
        };
        sourceArchetype.AddChunk(sourceChunk);

        var sourceStore = new EntityStore(entityCount);
        sourceStore.InstallTableImage(sourceRegistry);
        var entities = new Entity[entityCount];
        for (int row = 0; row < entityCount; row++)
        {
            Entity entity = sourceStore.Allocate();
            entities[row] = entity;
            Place(sourceStore, entity, sourceArchetype, sourceChunk, row);
        }

        ArchetypeRegistry firstRegistry = sourceRegistry.CloneExact(out var firstMap);
        EntityStore firstStore = sourceStore.CloneExact(firstRegistry);
        Assert.Equal(3, sourceStore.RecordPageCount);
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[0].Index));
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[300].Index));
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[519].Index));

        _ = firstStore.GetRecord(entities[0]);
        Assert.False(sourceStore.SharesRecordPageWith(firstStore, entities[0].Index));
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[300].Index));
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[519].Index));

        ArchetypeRegistry secondRegistry = firstRegistry.CloneExact(out var secondMap);
        EntityStore secondStore = firstStore.CloneExact(secondRegistry);
        EntityRecord secondRecord = secondStore.GetRecordReadOnly(entities[300]);

        Assert.Same(
            secondMap.Remap(firstMap.Remap(sourceArchetype)),
            secondRecord.Archetype);
        Assert.Same(
            secondMap.Remap(firstMap.Remap(sourceChunk)),
            secondRecord.Chunk);
        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entities[519].Index));
        Assert.True(firstStore.SharesRecordPageWith(secondStore, entities[519].Index));
    }

    [Fact]
    public void EntityStoreFork_SharedRecordPagesDoNotRetainAncestorTableShells()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<PersistentEntityRecord>());

        var generation = CreateSurvivingRecordGeneration();

        CollectAllGenerations();

        Assert.False(generation.SourceArchetype.TryGetTarget(out _));
        Assert.False(generation.SourceChunk.TryGetTarget(out _));
        Assert.False(generation.FirstArchetype.TryGetTarget(out _));
        Assert.False(generation.FirstChunk.TryGetTarget(out _));

        EntityRecord resolved = generation.Store.GetRecordReadOnly(generation.Entity);
        Assert.Same(generation.Archetype, resolved.Archetype);
        Assert.Same(generation.Chunk, resolved.Chunk);
        GC.KeepAlive(generation.Store);
        GC.KeepAlive(generation.Archetype);
        GC.KeepAlive(generation.Chunk);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        EntityStore Store,
        Entity Entity,
        Archetype Archetype,
        Chunk Chunk,
        WeakReference<Archetype> SourceArchetype,
        WeakReference<Chunk> SourceChunk,
        WeakReference<Archetype> FirstArchetype,
        WeakReference<Chunk> FirstChunk) CreateSurvivingRecordGeneration()
    {
        var sourceRegistry = new ArchetypeRegistry();
        Archetype sourceArchetype = sourceRegistry.GetOrCreate([ComponentMetadata<Position>.Id]);
        var sourceChunk = new Chunk(1, sourceArchetype.ColumnOperations)
        {
            Count = 1,
            IndexInArchetype = 0,
        };
        sourceArchetype.AddChunk(sourceChunk);

        var sourceStore = new EntityStore(initialCapacity: 1);
        sourceStore.InstallTableImage(sourceRegistry);
        Entity entity = sourceStore.Allocate();
        Place(sourceStore, entity, sourceArchetype, sourceChunk, row: 0);

        ArchetypeRegistry firstRegistry = sourceRegistry.CloneExact(out var firstMap);
        EntityStore firstStore = sourceStore.CloneExact(firstRegistry);
        Archetype firstArchetype = firstMap.Remap(sourceArchetype);
        Chunk firstChunk = firstMap.Remap(sourceChunk);

        ArchetypeRegistry secondRegistry = firstRegistry.CloneExact(out var secondMap);
        EntityStore secondStore = firstStore.CloneExact(secondRegistry);
        Archetype secondArchetype = secondMap.Remap(firstArchetype);
        Chunk secondChunk = secondMap.Remap(firstChunk);

        Assert.True(sourceStore.SharesRecordPageWith(firstStore, entity.Index));
        Assert.True(firstStore.SharesRecordPageWith(secondStore, entity.Index));
        Assert.Equal(
            sourceStore.StoredRecordSnapshot(entity.Index).ArchetypeIdentity,
            secondStore.StoredRecordSnapshot(entity.Index).ArchetypeIdentity);
        Assert.Equal(
            sourceStore.StoredRecordSnapshot(entity.Index).ChunkIdentity,
            secondStore.StoredRecordSnapshot(entity.Index).ChunkIdentity);

        _ = secondRegistry;
        return (
            secondStore,
            entity,
            secondArchetype,
            secondChunk,
            new WeakReference<Archetype>(sourceArchetype),
            new WeakReference<Chunk>(sourceChunk),
            new WeakReference<Archetype>(firstArchetype),
            new WeakReference<Chunk>(firstChunk));
    }

    private static void CollectAllGenerations()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void AssertAddTransitionCache(
        Archetype source,
        Archetype candidate,
        int componentId,
        DetachedTableMap map)
    {
        Assert.Equal(source.AddTransitionCount, candidate.AddTransitionCount);
        bool sourceHas = source.TryGetAddTransition(componentId, out StructuralTransition sourceEdge);
        bool candidateHas =
            candidate.TryGetAddTransition(componentId, out StructuralTransition candidateEdge);
        Assert.Equal(sourceHas, candidateHas);
        if (sourceHas)
            AssertTransitionClone(sourceEdge, candidateEdge, map);
    }

    private static void AssertRemoveTransitionCache(
        Archetype source,
        Archetype candidate,
        int componentId,
        DetachedTableMap map)
    {
        Assert.Equal(source.RemoveTransitionCount, candidate.RemoveTransitionCount);
        bool sourceHas =
            source.TryGetRemoveTransition(componentId, out StructuralTransition sourceEdge);
        bool candidateHas =
            candidate.TryGetRemoveTransition(componentId, out StructuralTransition candidateEdge);
        Assert.Equal(sourceHas, candidateHas);
        if (sourceHas)
            AssertTransitionClone(sourceEdge, candidateEdge, map);
    }

    private static void AssertIncludeCache(
        Archetype source,
        Archetype candidate,
        ReadOnlySpan<int> componentIds,
        DetachedTableMap map)
    {
        Assert.Equal(source.IncludeTransitionCount, candidate.IncludeTransitionCount);
        bool sourceHas =
            source.TryGetIncludeTransition(componentIds, out StructuralTransition sourceTransition);
        bool candidateHas =
            candidate.TryGetIncludeTransition(
                componentIds,
                out StructuralTransition candidateTransition);
        Assert.Equal(sourceHas, candidateHas);
        if (sourceHas)
            AssertTransitionClone(sourceTransition, candidateTransition, map);
    }

    private static void AssertTransitionClone(
        StructuralTransition source,
        StructuralTransition candidate,
        DetachedTableMap map)
    {
        Assert.Same(map.Remap(source.Target), candidate.Target);
        Assert.True(map.IsCandidate(candidate.Target));
        AssertDistinctBacking(source.SharedColumns, candidate.SharedColumns);
        AssertMappingsEqual(source.SharedColumns, candidate.SharedColumns);
    }

    private static void AssertMappingsEqual(
        ReadOnlySpan<SharedColumnMapping> source,
        ReadOnlySpan<SharedColumnMapping> candidate)
    {
        Assert.Equal(source.Length, candidate.Length);
        for (int index = 0; index < source.Length; index++)
        {
            Assert.Equal(source[index].SourceColumnIndex, candidate[index].SourceColumnIndex);
            Assert.Equal(source[index].DestinationColumnIndex, candidate[index].DestinationColumnIndex);
        }
    }

    private static void AssertSpansEqual<T>(ReadOnlySpan<T> source, ReadOnlySpan<T> candidate)
    {
        Assert.Equal(source.Length, candidate.Length);
        for (int index = 0; index < source.Length; index++)
            Assert.Equal(source[index], candidate[index]);
    }

    private static void AssertDistinctBacking<T>(ReadOnlySpan<T> source, ReadOnlySpan<T> candidate)
    {
        Assert.Equal(source.Length, candidate.Length);
        if (source.IsEmpty)
            return;

        Assert.False(Unsafe.AreSame(
            ref Unsafe.AsRef(in source[0]),
            ref Unsafe.AsRef(in candidate[0])));
    }

    private static void AssertSameBacking<T>(ReadOnlySpan<T> source, ReadOnlySpan<T> candidate)
    {
        Assert.Equal(source.Length, candidate.Length);
        if (source.IsEmpty)
            return;

        Assert.True(Unsafe.AreSame(
            ref Unsafe.AsRef(in source[0]),
            ref Unsafe.AsRef(in candidate[0])));
    }

    private static void Place(
        EntityStore store,
        Entity entity,
        Archetype archetype,
        Chunk chunk,
        int row)
    {
        chunk.Entities[row] = entity;
        EntityRecordWriter record = store.GetRecord(entity);
        record.Archetype = archetype;
        record.Chunk = chunk;
        record.RowInChunk = row;
    }

}
