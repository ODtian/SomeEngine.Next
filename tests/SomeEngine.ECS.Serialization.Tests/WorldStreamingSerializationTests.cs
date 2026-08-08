using System.Reflection;
using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using DefaultHierarchy = SomeEngine.ECS.Hierarchy.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class WorldStreamingSerializationTests
{
    [Fact]
    public void AdmittedWorldWrite_SuccessAndOutputFault_DoNotAdvanceTopologyRevision()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        long expectedRevision = world.PublishedTopologyRevision;
        long expectedEpoch = world.PublishedStructureEpoch;
        object originalRoot = PublishedStructureRoot(world);

        using var successful = new MemoryStream();
        WorldSerializer.WriteWorld(successful, world, registry);
        object successfulRoot = PublishedStructureRoot(world);
        Assert.NotSame(originalRoot, successfulRoot);
        Assert.Equal(expectedRevision, world.PublishedTopologyRevision);
        Assert.Equal(expectedEpoch, world.PublishedStructureEpoch);

        using var faulting = new ThrowingWriteStream();
        Assert.Throws<IOException>(() => WorldSerializer.WriteWorld(faulting, world, registry));
        object faultRoot = PublishedStructureRoot(world);
        Assert.NotSame(successfulRoot, faultRoot);
        Assert.Equal(expectedRevision, world.PublishedTopologyRevision);
        Assert.Equal(expectedEpoch, world.PublishedStructureEpoch);

        // The owner-thread root context must be gone after the output exception. A subsequent
        // capture on this same test thread must pin the current successor, not collide with or
        // read through the failed capture's retained root.
        using var retry = new MemoryStream();
        WorldSerializer.WriteWorld(retry, world, registry);
        Assert.NotSame(faultRoot, PublishedStructureRoot(world));
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(expectedRevision, world.PublishedTopologyRevision);
        Assert.Equal(expectedEpoch, world.PublishedStructureEpoch);
    }

    [Theory]
    [InlineData(WriteFailureKind.Io)]
    [InlineData(WriteFailureKind.Canceled)]
    [InlineData(WriteFailureKind.Disposed)]
    public async Task WorldWrite_StreamFailureReleasesAdmission(WriteFailureKind kind)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Exception expected = kind switch
        {
            WriteFailureKind.Io => new IOException("Injected output fault."),
            WriteFailureKind.Canceled => new OperationCanceledException("Injected cancellation."),
            WriteFailureKind.Disposed => new ObjectDisposedException("destination"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var output = new FaultingWriteStream(expected);

        Exception? actual = Record.Exception(() =>
            WorldSerializer.WriteWorld(output, world, registry));
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public async Task WorldWrite_ComponentCodecFailureReleasesAdmission()
    {
        var registry = new SerializationRegistry()
            .Register<ThrowingWorldComponent, ThrowingWorldComponentCodec>();
        var world = new World();
        world.CreateEntity(new ThrowingWorldComponent { Value = 1 });
        using var output = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(output, world, registry));
        Assert.Contains("Injected codec fault", error.Message);
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public void WorldWrite_ComponentCodecReentryMutatesSuccessorWithoutChangingRetainedOutput()
    {
        var registry = new SerializationRegistry()
            .Register<ReentrantWorldComponent, ReentrantWorldComponentCodec>();
        using var world = new World();
        world.CreateEntity(new ReentrantWorldComponent { Value = 1 });
        using var output = new MemoryStream();
        ReentrantWorldComponentCodec.Target = world;
        try
        {
            WorldSerializer.WriteWorld(output, world, registry);
        }
        finally
        {
            ReentrantWorldComponentCodec.Target = null;
        }

        Assert.Equal(2, world.EntityCount);
        output.Position = 0;
        using World restored = WorldSerializer.ReadWorld(output, registry);
        Assert.Equal(1, restored.EntityCount);
    }

    [Fact]
    public void WorldWrite_OutputCallbackMutatesPublishedSuccessor()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity created = default;
        using var output = new CallbackWriteStream(() => created = world.CreateEntity());

        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(world.IsAlive(created));
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void WorldWrite_OutputCallbackCanDisposeAnUnrelatedWorld()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var serializedWorld = new World();
        serializedWorld.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var otherWorld = new World();
        using var output = new CallbackWriteStream(otherWorld.Dispose);

        WorldSerializer.WriteWorld(output, serializedWorld, registry);

        Assert.Equal(1, serializedWorld.EntityCount);
        Assert.Throws<ObjectDisposedException>(() => otherWorld.CreateEntity());
    }

    [Fact]
    public void WriteEntities_InvalidMemberFailsBeforeOutputWithoutPublishingSuccessor()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity valid = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var invalid = new Entity(valid.Index + 100, valid.Generation);
        object rootBefore = PublishedStructureRoot(world);
        long epochBefore = world.PublishedStructureEpoch;
        long revisionBefore = world.PublishedTopologyRevision;
        using var output = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteEntities(output, world, [valid, invalid], registry));

        Assert.Contains("not alive", error.Message);
        Assert.Equal(0, output.Length);
        Assert.Same(rootBefore, PublishedStructureRoot(world));
        Assert.Equal(epochBefore, world.PublishedStructureEpoch);
        Assert.Equal(revisionBefore, world.PublishedTopologyRevision);
        Assert.True(world.IsAlive(valid));
    }

    [Fact]
    public async Task SnapshotControlPlane_SerializesConcurrentTopologyWriteWithoutRevisionBump()
    {
        var world = new World();
        using var admission = new BlockingSnapshotAdmission();
        world.BindJobAdmission(admission);
        long revisionBeforeCapture = world.PublishedTopologyRevision;

        Task capture = Task.Run(() =>
        {
            using var output = new MemoryStream();
            WorldSerializer.WriteWorld(output, world, new SerializationRegistry());
        });
        Assert.True(admission.WaitUntilSnapshotEntered(TimeSpan.FromSeconds(10)));

        Task<Entity> mutation = Task.Run(() => world.CreateEntity());
        try
        {
            Task completed = await Task.WhenAny(
                mutation,
                Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(mutation, completed);
            Assert.Equal(revisionBeforeCapture, world.PublishedTopologyRevision);
        }
        finally
        {
            admission.ReleaseSnapshot();
        }

        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        Entity created = await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.IsAlive(created));
        Assert.Equal(revisionBeforeCapture + 1, world.PublishedTopologyRevision);
    }

    [Theory]
    [InlineData(ActiveCaptureOperation.Entity)]
    [InlineData(ActiveCaptureOperation.Query)]
    [InlineData(ActiveCaptureOperation.World)]
    [InlineData(ActiveCaptureOperation.Checkpoint)]
    public void SerializationCapture_InsideActiveStructuralCandidateFailsBeforeOutput(
        ActiveCaptureOperation operation)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<SerPosition>());
        object publishedRoot = PublishedStructureRoot(world);
        long publishedEpoch = PublishedStructureEpoch(world);
        long publishedRevision = world.PublishedTopologyRevision;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            object candidateRoot = world.ActiveStructureRoot;
            using var output = new MemoryStream();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case ActiveCaptureOperation.Entity:
                        WorldSerializer.WriteEntity(output, world, entity, registry);
                        break;
                    case ActiveCaptureOperation.Query:
                        WorldSerializer.WriteQuery(output, world, query, registry);
                        break;
                    case ActiveCaptureOperation.World:
                        WorldSerializer.WriteWorld(output, world, registry);
                        break;
                    case ActiveCaptureOperation.Checkpoint:
                        WorldCheckpointCodec.Write(output, world, registry);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            });

            Assert.Contains("active structural transaction", error.Message);
            Assert.Equal(0, output.Length);
            Assert.Same(candidateRoot, world.ActiveStructureRoot);
            Assert.Same(publishedRoot, PublishedStructureRoot(world));
            Assert.Equal(publishedEpoch, PublishedStructureEpoch(world));
            Assert.Equal(publishedRevision, world.PublishedTopologyRevision);
            Assert.Equal(1, world.Read<SerPosition>(entity).X);
        }

        Assert.Same(publishedRoot, PublishedStructureRoot(world));
        Assert.Equal(publishedEpoch, PublishedStructureEpoch(world));
        Assert.Equal(publishedRevision, world.PublishedTopologyRevision);
    }

    [Fact]
    public void SerializationCapture_InsidePreCandidateStructuralTransactionFailsBeforeOutput()
    {
        var world = new World();
        object publishedRoot = PublishedStructureRoot(world);
        long publishedEpoch = PublishedStructureEpoch(world);
        using (World.StructuralTransactionScope transaction =
               world.BeginStructuralTransaction())
        using (var output = new MemoryStream())
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                WorldSerializer.WriteWorld(
                    output,
                    world,
                    new SerializationRegistry()));

            Assert.Contains("active structural transaction", error.Message);
            Assert.Equal(0, output.Length);
            Assert.Same(publishedRoot, PublishedStructureRoot(world));
            Assert.Equal(publishedEpoch, PublishedStructureEpoch(world));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TopologyValidation_WorldMutationUsesTheOrdinaryPublishedRoot(
        bool useDifferentWorld)
    {
        using var serializedWorld = new World();
        var targetWorld = useDifferentWorld ? new World() : serializedWorld;
        try
        {
            var registry = new SerializationRegistry();
            RegisterTopologyRuntime(
                registry,
                new ValidationReentryTopologyRuntime(() => targetWorld.CreateEntity()));
            using var output = new MemoryStream();

            WorldSerializer.WriteWorld(output, serializedWorld, registry);

            Assert.Equal(1, targetWorld.EntityCount);
            output.Position = 0;
            using World restored = WorldSerializer.ReadWorld(output, registry);
            Assert.Equal(useDifferentWorld ? 0 : 1, restored.EntityCount);
        }
        finally
        {
            if (useDifferentWorld)
                targetWorld.Dispose();
        }
    }

    [Fact]
    public void TopologyValidation_RecursiveSerializationOfSameWorldRejectsStaleCapture()
    {
        using var world = new World();
        using var nestedOutput = new MemoryStream();
        var registry = new SerializationRegistry();
        RegisterTopologyRuntime(
            registry,
            new ValidationReentryTopologyRuntime(() =>
                WorldSerializer.WriteWorld(
                    nestedOutput,
                    world,
                    new SerializationRegistry())));
        using var output = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(output, world, registry));

        Assert.Contains("source root changed", error.Message);
        Assert.Equal(0, output.Length);
        Assert.True(nestedOutput.Length > 0);
    }

    [Fact]
    public void TopologyValidation_RecursiveSerializationOfDifferentWorldIsIndependent()
    {
        using var serializedWorld = new World();
        using var targetWorld = new World();
        using var nestedOutput = new MemoryStream();
        var registry = new SerializationRegistry();
        RegisterTopologyRuntime(
            registry,
            new ValidationReentryTopologyRuntime(() =>
                WorldSerializer.WriteWorld(
                    nestedOutput,
                    targetWorld,
                    new SerializationRegistry())));
        using var output = new MemoryStream();

        WorldSerializer.WriteWorld(output, serializedWorld, registry);

        Assert.True(output.Length > 0);
        Assert.True(nestedOutput.Length > 0);
    }

    [Fact]
    public void TopologyEncoding_InvokesCodecExactlyOnce()
    {
        var registry = new SerializationRegistry();
        var runtime = new AlternatingTopologyRuntime();
        MethodInfo registerTopology = typeof(SerializationRegistry).GetMethod(
            "RegisterTopology",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        registerTopology.Invoke(registry, [runtime]);

        using var output = new CountingNonSeekableWriteStream();
        WorldSerializer.WriteWorld(output, new World(), registry);

        Assert.Equal(1, runtime.WriteCount);
    }

    [Fact]
    public void SnapshotOwnershipHandoff_PreservesMetricsAndQueries()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var query = world.Query(world.QueryDefinition().Read<SerPosition>());
        WorldStructuralMetrics metricsBefore = world.GetStructuralMetrics();
        object rootBeforeSnapshot = PublishedStructureRoot(world);
        long epochBeforeSnapshot = world.PublishedStructureEpoch;
        long revisionBeforeSnapshot = world.PublishedTopologyRevision;

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, world, registry);

        WorldStructuralMetrics metricsAfter = world.GetStructuralMetrics();
        Assert.NotSame(rootBeforeSnapshot, PublishedStructureRoot(world));
        Assert.Equal(epochBeforeSnapshot, world.PublishedStructureEpoch);
        Assert.Equal(revisionBeforeSnapshot, world.PublishedTopologyRevision);
        Assert.Equal(metricsBefore.Started, metricsAfter.Started);
        Assert.Equal(metricsBefore.Published, metricsAfter.Published);
        Assert.Equal(metricsBefore.Aborted, metricsAfter.Aborted);
        Assert.Equal(metricsBefore.ClonedArchetypeShells, metricsAfter.ClonedArchetypeShells);
        Assert.Equal(metricsBefore.ClonedChunkShells, metricsAfter.ClonedChunkShells);
        Assert.Equal(metricsBefore.ClonedQueryMatches, metricsAfter.ClonedQueryMatches);

        int matched = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(entity, row.Entity);
                matched++;
            }
        });
        Assert.Equal(1, matched);
    }

    [Fact]
    public void SnapshotSuccessor_SkipsDerivedCachesAndSharesValueBackingUntilMutation()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.CreateEntity();
        world.Add(entity, new SerPosition { X = 1, Y = 2 });
        WorldStructureRoot sourceRoot = world.PublishedStructureRoot;
        Archetype sourceArchetype = sourceRoot.Tables.All.ToArray().Single(
            static archetype => archetype.Chunks.ToArray().Any(static chunk => chunk.Count != 0));
        Chunk sourceChunk =
            sourceArchetype.Chunks.ToArray().Single(static chunk => chunk.Count != 0);
        Assert.True(
            sourceRoot.Tables.All.ToArray().Sum(
                static archetype => archetype.AddTransitionCount) > 0);

        using var output = new MemoryStream();
        WorldSerializer.WriteWorld(output, world, registry);

        WorldStructureRoot successor = world.PublishedStructureRoot;
        Archetype successorArchetype = successor.Tables.All.ToArray().Single(
            archetype => archetype.PersistentIdentity == sourceArchetype.PersistentIdentity);
        Chunk successorChunk = successorArchetype.Chunks.ToArray().Single(
            chunk => chunk.PersistentIdentity == sourceChunk.PersistentIdentity);
        Assert.All(successor.Tables.All.ToArray(), static archetype =>
        {
            Assert.Equal(0, archetype.AddTransitionCount);
            Assert.Equal(0, archetype.RemoveTransitionCount);
            Assert.Equal(0, archetype.IncludeTransitionCount);
            Assert.False(archetype.HasCleanupTransition);
        });
        Assert.True(sourceChunk.SharesStorageWith(successorChunk));

        world.Replace(entity, new SerPosition { X = 9, Y = 10 });

        Assert.False(sourceChunk.SharesStorageWith(successorChunk));
        Assert.Equal(9, world.Read<SerPosition>(entity).X);
    }

    [Fact]
    public void CanonicalWorldStateHash_IsStableAndChangesWithState()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var first = new World();
        Entity firstEntity = first.CreateEntity(new SerPosition { X = 1, Y = 2 });
        first.AddSparse(firstEntity, new SerSparse { Value = 3 });
        var second = new World();
        Entity secondEntity = second.CreateEntity(new SerPosition { X = 1, Y = 2 });
        second.AddSparse(secondEntity, new SerSparse { Value = 3 });

        Digest256 firstHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 repeatedHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 equivalentHash = WorldSerializer.ComputeWorldStateHash(second, registry);

        Assert.Equal(firstHash, repeatedHash);
        Assert.Equal(firstHash, equivalentHash);

        second.Replace(secondEntity, new SerPosition { X = 9, Y = 2 });
        Digest256 mutatedHash = WorldSerializer.ComputeWorldStateHash(second, registry);
        Assert.NotEqual(firstHash, mutatedHash);
    }

    [Fact]
    public async Task BlockedOutput_AllowsSuccessorMutationAndKeepsSerializedBytesStable()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity captured = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var output = new GateWriteStream();

        Task write = Task.Run(() => WorldSerializer.WriteWorld(output, world, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        Task<Entity> mutate = Task.Run(() =>
        {
            world.Replace(captured, new SerPosition { X = 10, Y = 20 });
            return world.CreateEntity(new SerPosition { X = 30, Y = 40 });
        });
        Entity createdAfterWrite;
        try
        {
            createdAfterWrite = await mutate.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(10, world.Read<SerPosition>(captured).X);
            Assert.True(world.IsAlive(createdAfterWrite));
        }
        finally
        {
            output.Release();
        }
        await write.WaitAsync(TimeSpan.FromSeconds(10));

        using var input = new MemoryStream(output.ToArray(), writable: false);
        using World restored = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(1, restored.EntityCount);
        Assert.True(restored.IsAlive(captured));
        Assert.Equal(1, restored.Read<SerPosition>(captured).X);
    }

    [Theory]
    [InlineData(NarrowCaptureOperation.Entity)]
    [InlineData(NarrowCaptureOperation.EntitySet)]
    [InlineData(NarrowCaptureOperation.Query)]
    public async Task NarrowWrite_BlockedOutputAllowsMutationAndUsesPinnedRoot(
        NarrowCaptureOperation operation)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity first = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity second = world.CreateEntity(new SerPosition { X = 3, Y = 4 });
        Entity[] selected = [first, second];
        QueryHandle query = world.Query(world.QueryDefinition().Read<SerPosition>());

        using var baseline = new MemoryStream();
        WriteNarrow(operation, baseline, world, first, selected, query, registry);

        using var output = new GateWriteStream();
        Task write = Task.Run(() =>
            WriteNarrow(operation, output, world, first, selected, query, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        try
        {
            await Task.Run(() =>
            {
                world.Replace(first, new SerPosition { X = 10, Y = 20 });
                world.DestroyEntity(second);
                world.CreateEntity(new SerPosition { X = 30, Y = 40 });
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(10, world.Read<SerPosition>(first).X);
            Assert.False(world.IsAlive(second));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            output.Release();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(baseline.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task WorldWrite_BlockedOutputMakesDisposeWaitThenCompletes()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var output = new GateWriteStream();

        Task write = Task.Run(() => WorldSerializer.WriteWorld(output, world, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(world.Dispose);
        try
        {
            Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(dispose, completed);
        }
        finally
        {
            output.Release();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity());
    }

    [Fact]
    public void WorldWrite_InvokesComponentAndBufferCodecsExactlyOncePerValue()
    {
        CountingWorldComponentCodec.Reset();
        CountingWorldBufferCodec.Reset();
        var registry = new SerializationRegistry()
            .Register<CountingWorldComponent, CountingWorldComponentCodec>()
            .RegisterBuffer<CountingWorldBuffer, CountingWorldBufferCodec>();
        var world = new World();
        world.CreateEntity(new CountingWorldComponent { Value = 1 });
        world.CreateEntity(new CountingWorldComponent { Value = 2 });
        Entity buffered = world.CreateEntity();
        world.AddBuffer<CountingWorldBuffer>(buffered);
        int[] values = [3, 4, 5];
        world.ExecuteBufferWrite<CountingWorldBuffer, int[]>(
            buffered,
            ref values,
            static (DynamicBuffer<CountingWorldBuffer> buffer, ref int[] source) =>
            {
                for (int i = 0; i < source.Length; i++)
                    buffer.Add(new CountingWorldBuffer { Value = source[i] });
            });

        using var output = new MemoryStream();
        WorldSerializer.WriteWorld(output, world, registry);

        Assert.Equal(2, CountingWorldComponentCodec.WriteCount);
        Assert.Equal(3, CountingWorldBufferCodec.WriteCount);
    }

    [Fact]
    public void LargeArchetypeAndSparseWorld_WritesThroughBoundedNonSeekableFrames()
    {
        const int entityCount = 20_000;
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var world = new World(entityCount);
        for (int i = 0; i < entityCount; i++)
        {
            Entity entity = world.CreateEntity(new SerPosition { X = i, Y = -i });
            if (i % 3 == 0)
                world.AddSparse(entity, new SerSparse { Value = i });
        }

        using var output = new CountingNonSeekableWriteStream();
        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(output.BytesWritten > entityCount * 20L);
        // Whole-world buffering would arrive as a multi-megabyte write. The streaming writer's
        // largest request is one header string or one item frame, independent of entity count.
        Assert.InRange(output.MaximumWriteSize, 1, 512);
    }

    [Fact]
    public void SparseMembershipWorkingSet_RespectsConfiguredLimit()
    {
        var registry = new SerializationRegistry().RegisterSparse<SerSparse>();
        var world = new World();
        for (int i = 0; i < 3; i++)
        {
            Entity entity = world.CreateEntity();
            world.AddSparse(entity, new SerSparse { Value = i });
        }

        using var rejected = new MemoryStream();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumSparseMemberships: 2)));
        Assert.Contains("sparse membership count", error.Message);

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumSparseMemberships: 3));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public async Task WholeWorldSnapshot_RejectsManagedReferenceValuesBeforeOutput()
    {
        var registry = new SerializationRegistry()
            .Register<SnapshotReferenceComponent, SnapshotReferenceCodec>();
        int[] values = [1, 2, 3];
        var world = new World();
        Entity entity = world.CreateEntity(new SnapshotReferenceComponent { Values = values });
        long revisionBefore = world.PublishedTopologyRevision;
        WorldStructuralMetrics metricsBefore = world.GetStructuralMetrics();
        using var output = new GateWriteStream();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => WorldSerializer.WriteWorld(output, world, registry)));

        Assert.Contains("deep snapshot-clone contract", error.Message);
        Assert.False(output.WaitUntilWrite(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(revisionBefore, world.PublishedTopologyRevision);
        Assert.Equal(metricsBefore, world.GetStructuralMetrics());
        values[0] = 99;
        Assert.Equal(99, world.Read<SnapshotReferenceComponent>(entity).Values![0]);
    }

    [Fact]
    public void WholeWorldSnapshot_AllowsRegisteredManagedReferenceTypeWhenAbsent()
    {
        var registry = new SerializationRegistry()
            .Register<SnapshotReferenceComponent, SnapshotReferenceCodec>();
        var world = new World();
        Entity entity = world.CreateEntity();
        long revisionBefore = world.PublishedTopologyRevision;
        WorldStructuralMetrics metricsBefore = world.GetStructuralMetrics();
        using var output = new MemoryStream();

        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(output.Length > 0);
        Assert.True(world.IsAlive(entity));
        Assert.False(world.Has<SnapshotReferenceComponent>(entity));
        Assert.Equal(revisionBefore, world.PublishedTopologyRevision);
        Assert.Equal(metricsBefore, world.GetStructuralMetrics());
    }

    [Fact]
    public void TopologyWorkingSet_RespectsRecordAndPayloadLimits_OnNonSeekableOutput()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>();
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        DefaultHierarchy.SetParent(world, first, parent);
        DefaultHierarchy.SetParent(world, second, parent);

        using var recordRejected = new CountingNonSeekableWriteStream();
        InvalidOperationException recordError = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                recordRejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 1)));
        Assert.Contains("topology record count", recordError.Message);

        using var payloadRejected = new CountingNonSeekableWriteStream();
        InvalidOperationException payloadError = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                payloadRejected,
                world,
                registry,
                new SerializeOptions(
                    MaximumTopologyRecords: 16,
                    MaximumTopologyPayloadBytes: 1)));
        Assert.Contains("topology payload bytes", payloadError.Message);

        using var accepted = new CountingNonSeekableWriteStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(
                MaximumTopologyRecords: 16,
                MaximumTopologyPayloadBytes: 1024));
        Assert.InRange(accepted.MaximumWriteSize, 1, 512);
    }

    [Fact]
    public void HierarchyRecordBudget_RejectsTheDirectWriteWithoutSnapshotDto()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>();
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(
            world,
            parent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, first, parent);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, second, parent);
        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 4)));

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumTopologyRecords: 5));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public void RelationTopologyWrite_VisitsOnlyEdgesAndActualOrderedShards()
    {
        var registry = new SerializationRegistry()
            .Register<StreamingRelation>()
            .RegisterRelationTopology<StreamingRelation>();
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        world.SetRelationAdjacencyOrder<StreamingRelation>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        world.SetRelationAdjacencyOrder<StreamingRelation>(
            target,
            RelationAdjacencyRole.Incoming,
            RelationAdjacencyOrderPolicy.Ordered);
        world.CreateRelation(source, target, new StreamingRelation { Value = 1 });
        for (int i = 0; i < 10_000; i++)
            world.CreateEntity();

        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 4)));
        Assert.Equal(default, world.GetRelationTopologyWriteDiagnostics<StreamingRelation>());

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumTopologyRecords: 5));
        RelationTopologyWriteDiagnostics diagnostics =
            world.GetRelationTopologyWriteDiagnostics<StreamingRelation>();
        Assert.Equal(1, diagnostics.WriteCount);
        Assert.Equal(1, diagnostics.EdgeVisits);
        Assert.Equal(2, diagnostics.OrderedShardVisits);
    }

    [Fact]
    public void TopologyPayloadBudget_StopsTheSingleWriteAtTheFirstExcessByte()
    {
        var registry = new SerializationRegistry();
        var runtime = new OversizedTopologyRuntime();
        MethodInfo registerTopology = typeof(SerializationRegistry).GetMethod(
            "RegisterTopology",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        registerTopology.Invoke(registry, [runtime]);

        using var output = new CountingNonSeekableWriteStream();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                output,
                new World(),
                registry,
                new SerializeOptions(MaximumTopologyPayloadBytes: 4)));

        Assert.Contains("topology payload bytes", error.Message);
        Assert.Equal(5, runtime.ByteAttempts);
        Assert.Equal(1, runtime.WriteCount);
    }

    [Fact]
    public void TemporaryWorldFailure_PreservesRestoreAndDisposeFaults()
    {
        var restoreFailure = new InvalidDataException("restore");
        var disposeFailure = new IOException("dispose");

        AggregateException aggregate = Assert.Throws<AggregateException>(() =>
            WorldSerializer.RethrowAfterTemporaryWorldFailure(
                restoreFailure,
                () => throw disposeFailure));
        Assert.Collection(
            aggregate.InnerExceptions,
            error => Assert.Same(restoreFailure, error),
            error => Assert.Same(disposeFailure, error));

        Exception rethrown = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.RethrowAfterTemporaryWorldFailure(
                restoreFailure,
                static () => { }));
        Assert.Same(restoreFailure, rethrown);
    }

    [Fact]
    public void RepeatedSnapshotAfterEquivalentWrites_RemainsByteDeterministic()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var world = new World();
        Entity first = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity second = world.CreateEntity(new SerPosition { X = 3, Y = 4 });
        world.AddSparse(second, new SerSparse { Value = 5 });

        using var beforeMutation = new MemoryStream();
        WorldSerializer.WriteWorld(beforeMutation, world, registry);

        world.Replace(first, new SerPosition { X = 100, Y = 200 });
        world.Replace(first, new SerPosition { X = 1, Y = 2 });

        using var afterMutation = new MemoryStream();
        WorldSerializer.WriteWorld(afterMutation, world, registry);
        Assert.Equal(beforeMutation.ToArray(), afterMutation.ToArray());
    }

    private static object PublishedStructureRoot(World world) =>
        typeof(World).GetProperty(
            "PublishedStructureRoot",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    private static long PublishedStructureEpoch(World world) =>
        (long)typeof(World).GetProperty(
            "PublishedStructureEpoch",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    private static void RegisterTopologyRuntime(
        SerializationRegistry registry,
        TopologySerializationRuntime runtime)
    {
        MethodInfo registerTopology = typeof(SerializationRegistry).GetMethod(
            "RegisterTopology",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        registerTopology.Invoke(registry, [runtime]);
    }

    private static async Task AssertMutationCompletesAsync(World world)
    {
        Entity created = await Task.Run(() => world.CreateEntity())
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.IsAlive(created));
    }

    private static void WriteNarrow(
        NarrowCaptureOperation operation,
        Stream output,
        World world,
        Entity entity,
        Entity[] entities,
        QueryHandle query,
        SerializationRegistry registry)
    {
        switch (operation)
        {
            case NarrowCaptureOperation.Entity:
                WorldSerializer.WriteEntity(output, world, entity, registry);
                break;
            case NarrowCaptureOperation.EntitySet:
                WorldSerializer.WriteEntities(output, world, entities, registry);
                break;
            case NarrowCaptureOperation.Query:
                WorldSerializer.WriteQuery(output, world, query, registry);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    public enum WriteFailureKind
    {
        Io,
        Canceled,
        Disposed,
    }

    public enum ActiveCaptureOperation
    {
        Entity,
        Query,
        World,
        Checkpoint,
    }

    public enum NarrowCaptureOperation
    {
        Entity,
        EntitySet,
        Query,
    }

    private struct ThrowingWorldComponent : IComponent
    {
        public int Value;
    }

    private struct ThrowingWorldComponentCodec : IComponentCodec<ThrowingWorldComponent>
    {
        public void Write(ref DataWriter writer, in ThrowingWorldComponent value) =>
            throw new InvalidOperationException("Injected codec fault.");

        public void Read(ref DataReader reader, out ThrowingWorldComponent value) =>
            value = new ThrowingWorldComponent { Value = reader.ReadInt32() };
    }

    private struct ReentrantWorldComponent : IComponent
    {
        public int Value;
    }

    private struct ReentrantWorldComponentCodec : IComponentCodec<ReentrantWorldComponent>
    {
        internal static World? Target { get; set; }

        public void Write(ref DataWriter writer, in ReentrantWorldComponent value)
        {
            Target!.CreateEntity();
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out ReentrantWorldComponent value) =>
            value = new ReentrantWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldComponent : IComponent
    {
        public int Value;
    }

    private struct CountingWorldComponentCodec : IComponentCodec<CountingWorldComponent>
    {
        private static int s_writeCount;

        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static void Reset() => Volatile.Write(ref s_writeCount, 0);

        public void Write(ref DataWriter writer, in CountingWorldComponent value)
        {
            Interlocked.Increment(ref s_writeCount);
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out CountingWorldComponent value) =>
            value = new CountingWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldBuffer : IBufferElement
    {
        public int Value;
    }

    private struct CountingWorldBufferCodec : IComponentCodec<CountingWorldBuffer>
    {
        private static int s_writeCount;

        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static void Reset() => Volatile.Write(ref s_writeCount, 0);

        public void Write(ref DataWriter writer, in CountingWorldBuffer value)
        {
            Interlocked.Increment(ref s_writeCount);
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out CountingWorldBuffer value) =>
            value = new CountingWorldBuffer { Value = reader.ReadInt32() };
    }

    private struct SnapshotReferenceComponent : IComponent
    {
        public int[]? Values;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct StreamingRelation : IComponent
    {
        public int Value;
    }

    private struct SnapshotReferenceCodec : IComponentCodec<SnapshotReferenceComponent>
    {
        public void Write(ref DataWriter writer, in SnapshotReferenceComponent value)
        {
            int[] values = value.Values ?? Array.Empty<int>();
            writer.WriteInt32(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.WriteInt32(values[i]);
        }

        public void Read(ref DataReader reader, out SnapshotReferenceComponent value)
        {
            int count = reader.ReadInt32();
            var values = new int[count];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadInt32();
            value = new SnapshotReferenceComponent { Values = values };
        }
    }

    private sealed class AlternatingTopologyRuntime : TopologySerializationRuntime
    {
        private int _writeCount;

        internal AlternatingTopologyRuntime()
            : base(
                TopologySerializationKind.Relation,
                typeof(SerPosition),
                new SerializationTypeKey(
                    Guid.Parse("4b6cdf55-f2e5-45a0-8a1c-d98d34627e40"),
                    "tests.alternating-topology",
                    1))
        {
        }

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal override void ValidateWriteState(AdmittedWorldWrite admitted)
        {
        }

        internal override void WriteAdmitted(
            BinaryWriter writer,
            AdmittedWorldWrite admitted,
            TopologyCaptureBudget budget)
        {
            budget.ReserveRecords(1, TypeKey.StableName);
            writer.Write(unchecked((byte)Interlocked.Increment(ref _writeCount)));
        }

        internal override void ReadApply(
            BinaryReader reader,
            SerializationReadBudget budget,
            World world,
            IReferenceRemapper? remapper)
        {
            reader.ReadByte();
        }
    }

    private sealed class ValidationReentryTopologyRuntime : TopologySerializationRuntime
    {
        private readonly Action _validation;

        internal ValidationReentryTopologyRuntime(Action validation)
            : base(
                TopologySerializationKind.Relation,
                typeof(ValidationReentryTopologyRuntime),
                new SerializationTypeKey(
                    Guid.Parse("A26BD7B7-38D9-45C0-8476-2B0B88615DAE"),
                    "tests.validation-reentry-topology",
                    0xA70D08CD86EF421Bul))
        {
            _validation = validation;
        }

        internal override void ValidateWriteState(AdmittedWorldWrite admitted) => _validation();

        internal override void WriteAdmitted(
            BinaryWriter writer,
            AdmittedWorldWrite admitted,
            TopologyCaptureBudget budget) =>
            budget.ReserveRecords(0, TypeKey.StableName);

        internal override void ReadApply(
            BinaryReader reader,
            SerializationReadBudget budget,
            World world,
            IReferenceRemapper? remapper)
        {
        }
    }

    private sealed class OversizedTopologyRuntime : TopologySerializationRuntime
    {
        private int _byteAttempts;
        private int _writeCount;

        internal OversizedTopologyRuntime()
            : base(
                TopologySerializationKind.Relation,
                typeof(SerPosition),
                new SerializationTypeKey(
                    Guid.Parse("69285009-616e-470e-809d-12cad1a09886"),
                    "tests.oversized-topology",
                    1))
        {
        }

        internal int ByteAttempts => Volatile.Read(ref _byteAttempts);
        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal override void ValidateWriteState(AdmittedWorldWrite admitted)
        {
        }

        internal override void WriteAdmitted(
            BinaryWriter writer,
            AdmittedWorldWrite admitted,
            TopologyCaptureBudget budget)
        {
            budget.ReserveRecords(0, TypeKey.StableName);
            Interlocked.Increment(ref _writeCount);
            for (int i = 0; i < 1024; i++)
            {
                Interlocked.Increment(ref _byteAttempts);
                writer.Write((byte)i);
            }
        }

        internal override void ReadApply(
            BinaryReader reader,
            SerializationReadBudget budget,
            World world,
            IReferenceRemapper? remapper)
        {
        }
    }

    private sealed class BlockingSnapshotAdmission : IWorldJobAdmission, IDisposable
    {
        private readonly SemaphoreSlim _exclusive = new(1, 1);
        private readonly ManualResetEventSlim _snapshotEntered = new(initialState: false);
        private readonly ManualResetEventSlim _releaseSnapshot = new(initialState: false);
        private int _blockNextSnapshot = 1;

        public bool HasCurrentJobScope => false;

        public bool HasCurrentThreadScope(World world) => false;

        internal bool WaitUntilSnapshotEntered(TimeSpan timeout) =>
            _snapshotEntered.Wait(timeout);

        internal void ReleaseSnapshot() => _releaseSnapshot.Set();

        public void Enter(World world, in WorldJobAdmissionRequest request)
        {
            _exclusive.Wait();
            try
            {
                bool snapshotControlPlane =
                    request.Topology == WorldTopologyAccess.Write &&
                    !request.BumpsTopologyRevision &&
                    !request.CanWrite;
                if (snapshotControlPlane &&
                    Interlocked.Exchange(ref _blockNextSnapshot, 0) != 0)
                {
                    _snapshotEntered.Set();
                    _releaseSnapshot.Wait();
                }
            }
            catch
            {
                _exclusive.Release();
                throw;
            }
        }

        public void Exit(World world, in WorldJobAdmissionRequest request) =>
            _exclusive.Release();

        public void ValidateCommandBufferAccess(World world)
        {
        }

        public void Dispose()
        {
            _releaseSnapshot.Set();
            _exclusive.Dispose();
            _snapshotEntered.Dispose();
            _releaseSnapshot.Dispose();
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected output fault.");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("Injected output fault.");
        public override void WriteByte(byte value) =>
            throw new IOException("Injected output fault.");
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FaultingWriteStream : Stream
    {
        private readonly Exception _failure;

        internal FaultingWriteStream(Exception failure) => _failure = failure;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) => throw _failure;
        public override void Write(ReadOnlySpan<byte> buffer) => throw _failure;
        public override void WriteByte(byte value) => throw _failure;
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class CallbackWriteStream : Stream
    {
        private readonly Action _callback;
        private int _invoked;

        internal CallbackWriteStream(Action callback) => _callback = callback;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) => Invoke();
        public override void Write(ReadOnlySpan<byte> buffer) => Invoke();
        public override void WriteByte(byte value) => Invoke();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Invoke()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0)
                _callback();
        }
    }

    private sealed class GateWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Exception? _failure;
        private int _blocked;

        internal GateWriteStream(Exception? failure = null) => _failure = failure;

        internal bool WaitUntilWrite(TimeSpan timeout) => _entered.Wait(timeout);

        internal void Release() => _release.Set();

        internal byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.WriteByte(value);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _inner.Dispose();
                _entered.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BlockFirstWrite()
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0)
                return;

            _entered.Set();
            _release.Wait();
        }
    }

    private sealed class GateReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _blocked;

        internal GateReadStream(byte[] bytes) =>
            _inner = new MemoryStream(bytes, writable: false);

        internal bool WaitUntilRead(TimeSpan timeout) => _entered.Wait(timeout);

        internal void Release() => _release.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            BlockFirstRead();
            return _inner.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            BlockFirstRead();
            return _inner.Read(buffer);
        }
        public override int ReadByte()
        {
            BlockFirstRead();
            return _inner.ReadByte();
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _inner.Dispose();
                _entered.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }
        private void BlockFirstRead()
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0)
                return;
            _entered.Set();
            _release.Wait();
        }
    }

    private sealed class CountingNonSeekableWriteStream : Stream
    {
        internal long BytesWritten { get; private set; }

        internal int MaximumWriteSize { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => Count(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);

        public override void WriteByte(byte value) => Count(1);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Count(int count)
        {
            BytesWritten += count;
            MaximumWriteSize = Math.Max(MaximumWriteSize, count);
        }
    }

    private sealed class NonSeekableInput : Stream
    {
        private readonly MemoryStream _inner;

        internal NonSeekableInput(byte[] bytes) =>
            _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
