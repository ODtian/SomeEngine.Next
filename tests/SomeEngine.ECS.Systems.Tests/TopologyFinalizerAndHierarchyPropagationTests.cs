using System.Collections.Concurrent;
using System.Reflection;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class TopologyFinalizerAndHierarchyPropagationTests
{
    [Fact]
    public void ParentPackets_RunConcurrently_ButPublishOnlyThroughOneFinalizer()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, oldParent);
            Hierarchy<Domain>.SetParent(world, second, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());

            using var arrived = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(arrived, release);
            long epoch = world.PublishedStructureEpoch;
            long topologyRevision = world.PublishedTopologyRevision;

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(newParent),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.True(arrived.Wait(TimeSpan.FromSeconds(3)));

            // Both packet spans already contain their edited values, but neither packet can
            // publish canonical Parent or Children.
            Assert.Equal(epoch, world.PublishedStructureEpoch);
            Assert.Equal(
                new HashSet<Entity> { first, second },
                Hierarchy<Domain>.GetChildren(world, oldParent).ToArray().ToHashSet());
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, newParent));

            release.Set();
            transaction.Handle.Complete();

            Assert.Equal(2, transaction.Partition.PacketCount);
            Assert.True(transaction.Partition.ProvesNonOverlap(0, 1));
            Assert.Equal(epoch, transaction.Partition.StructureEpoch);
            Assert.Equal(topologyRevision, transaction.Partition.TopologyRevision);
            Assert.True(Volatile.Read(ref PacketConcurrencyState.Maximum) >= 2);
            Assert.Equal(epoch + 1, world.PublishedStructureEpoch);
            Assert.True(world.PublishedTopologyRevision > topologyRevision);
            Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, second));
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, oldParent));
            Assert.Equal(
                new HashSet<Entity> { first, second },
                Hierarchy<Domain>.GetChildren(world, newParent).ToArray().ToHashSet());
        });
    }

    [Fact]
    public void NoopParentPackets_DoNotAcquireAWorldWriterOrPublishARevision()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity competingParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);
            uint tickBefore = world.CurrentTick;

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingNoopParentPacketJob<Domain>(),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            // A no-op image must not register a late topology writer or validate a stale
            // preimage. The dependency-independent write can finish before the packet returns.
            Hierarchy<Domain>.SetParent(world, child, competingParent);
            long structureEpoch = world.PublishedStructureEpoch;
            long topologyRevision = world.PublishedTopologyRevision;
            releasePacket.Set();
            transaction.Handle.Complete();

            Assert.Equal(1, transaction.Partition.PacketCount);
            Assert.Equal(tickBefore, world.CurrentTick);
            Assert.Equal(structureEpoch, world.PublishedStructureEpoch);
            Assert.Equal(topologyRevision, world.PublishedTopologyRevision);
            Assert.Equal(competingParent, Hierarchy<Domain>.GetParent(world, child));
        });
    }

    [Fact]
    public void ParentFinalizer_UsesOneLateCommitVersionAfterInterveningParentAba()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity abaParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Entity oldSibling = world.CreateEntity();
            Entity stagedSibling = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);
            Hierarchy<Domain>.SetParent(world, oldSibling, oldParent);
            Hierarchy<Domain>.SetParent(world, stagedSibling, stagedParent);
            world.AddTag<TopologySelectionTag>(child);
            QueryHandle query = world.Query(
                world.QueryDefinition()
                    .ReadWrite<Parent<Domain>>()
                    .All<TopologySelectionTag>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(stagedParent),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            _ = world.AcquireSystemTick();
            HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, abaParent))
                .Complete();
            HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, oldParent))
                .Complete();
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, child));
            uint interveningVersion = ComponentRowWriteVersion<Parent<Domain>>(world, child);

            releasePacket.Set();
            transaction.Handle.Complete();

            Assert.Equal(stagedParent, Hierarchy<Domain>.GetParent(world, child));
            uint commitVersion = ComponentRowWriteVersion<Parent<Domain>>(world, child);
            Assert.True(VersionClock.IsNewer(commitVersion, interveningVersion));
            Assert.Equal(
                commitVersion,
                ComponentChunkChangeVersion<Parent<Domain>>(world, child));
            Assert.Equal(
                commitVersion,
                ComponentRowWriteVersion<Children<Domain>>(world, oldParent));
            Assert.Equal(
                commitVersion,
                ComponentRowWriteVersion<Children<Domain>>(world, stagedParent));
            Assert.Equal(
                commitVersion,
                ComponentChunkChangeVersion<Children<Domain>>(world, oldParent));
            Assert.Equal(
                commitVersion,
                ComponentChunkChangeVersion<Children<Domain>>(world, stagedParent));

            QueryHandle changedParents = world.Query(
                world.QueryDefinition()
                    .Read<Parent<Domain>>()
                    .Changed<Parent<Domain>>());
            var changed = new List<Entity>();
            world.ExecuteQuery(
                changedParents,
                interveningVersion,
                world.CurrentTick,
                cursor =>
                {
                    foreach (QueryRow row in cursor.Rows)
                        changed.Add(row.Entity);
                });
            Assert.Equal([child], changed);
        });
    }

    [Fact]
    public void PacketFault_PreventsFinalizerAndLeavesLiveRootUntouched()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, oldParent);
            Hierarchy<Domain>.SetParent(world, second, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            long epoch = world.PublishedStructureEpoch;

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new FaultingParentPacketJob<Domain>(newParent),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.ThrowsAny<Exception>(() => transaction.Handle.Complete());

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);
            Assert.Contains("failed", partitionFault.Message, StringComparison.OrdinalIgnoreCase);
            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());
            Assert.Contains("failed", getPartitionFault.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(epoch, world.PublishedStructureEpoch);
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, second));
            Assert.Equal(
                new HashSet<Entity> { first, second },
                Hierarchy<Domain>.GetChildren(world, oldParent).ToArray().ToHashSet());
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, newParent));
        });
    }

    [Fact]
    public void FinalImageValidationFault_DiscardsEveryStagedParentAndInverseEdit()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, root);
            Hierarchy<Domain>.SetParent(world, second, first);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            long epoch = world.PublishedStructureEpoch;

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new ParentCyclePacketJob<Domain>(first, second),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.ThrowsAny<InvalidOperationException>(() => transaction.Handle.Complete());

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);
            Assert.Contains("failed", partitionFault.Message, StringComparison.OrdinalIgnoreCase);
            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());
            Assert.Contains("failed", getPartitionFault.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(epoch, world.PublishedStructureEpoch);
            Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
            Assert.Equal([first], Hierarchy<Domain>.GetChildren(world, root).ToArray());
            Assert.Equal([second], Hierarchy<Domain>.GetChildren(world, first).ToArray());
        });
    }

    [Fact]
    public void FaultedDependency_PermanentlyRejectsPartitionWithoutRunningCapturePackets()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            DependencyFaultState.PacketExecutions = 0;
            JobHandle dependency = JobSystem.Schedule(new FaultingDependencyJob());

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new CountingNoopParentPacketJob<Domain>(),
                dependency: dependency);

            Assert.ThrowsAny<Exception>(() => transaction.Handle.Complete());
            Assert.Equal(0, Volatile.Read(ref DependencyFaultState.PacketExecutions));

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);
            Assert.Contains("failed", partitionFault.Message, StringComparison.OrdinalIgnoreCase);
            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());
            Assert.Contains("failed", getPartitionFault.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TopologySchedule_DoesNotBlockAndCapturesTheDependencyFinalImage()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity dependencyParent = world.CreateEntity();
            Entity finalParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var writerStarted = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            ParentObservationState.Reset();

            JobHandle dependency = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new BlockingDeferredParentJob<Domain>(
                    world,
                    child,
                    dependencyParent,
                    writerStarted,
                    releaseWriter));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(3)));

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new ObserveAndReplaceParentPacketJob<Domain>(
                    dependencyParent,
                    finalParent),
                dependency: dependency);

            Assert.False(transaction.Handle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => _ = transaction.Partition);

            releaseWriter.Set();
            transaction.Handle.Complete();

            Assert.Equal(1, Volatile.Read(ref ParentObservationState.Matches));
            Assert.Equal(1, transaction.Partition.PacketCount);
            Assert.Equal(finalParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, oldParent));
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, dependencyParent));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, finalParent).ToArray());
        });
    }

    [Fact]
    public void WorldWriterMayRunDuringStaging_AndOptimisticPreimageConflictAbortsFinalizer()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity competingParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, competingParent))
                .Complete();

            releasePacket.Set();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => transaction.Handle.Complete());

            Assert.Contains("changed after", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(competingParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, oldParent).ToArray());
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, stagedParent));

            Hierarchy<Domain>.Maintain(world);
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, oldParent));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, competingParent).ToArray());
        });
    }

    [Fact]
    public void UnrelatedTopologyWrite_MergesWithTheCapturedQueryTransaction()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity unrelatedParent = world.CreateEntity();
            Entity selected = world.CreateEntity();
            Entity unrelated = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, selected, oldParent);
            Hierarchy<Domain>.SetParent(world, unrelated, oldParent);
            world.AddTag<TopologySelectionTag>(selected);
            QueryHandle query = world.Query(
                world.QueryDefinition()
                    .ReadWrite<Parent<Domain>>()
                    .All<TopologySelectionTag>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            Hierarchy<Domain>.SetParent(world, unrelated, unrelatedParent);
            releasePacket.Set();
            transaction.Handle.Complete();

            Assert.Equal(stagedParent, Hierarchy<Domain>.GetParent(world, selected));
            Assert.Equal(unrelatedParent, Hierarchy<Domain>.GetParent(world, unrelated));
        });
    }

    [Fact]
    public void EquivalentParentAba_IsSerializableBeforeTheFinalizer()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity originalParent = world.CreateEntity();
            Entity intermediateParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, originalParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            Hierarchy<Domain>.SetParent(world, child, intermediateParent);
            Hierarchy<Domain>.SetParent(world, child, originalParent);
            releasePacket.Set();
            transaction.Handle.Complete();

            // The final logical preimage and exact membership match the capture, so the two
            // intervening writes can be serialized entirely before this transaction.
            Assert.Equal(stagedParent, Hierarchy<Domain>.GetParent(world, child));
        });
    }

    [Fact]
    public void SameCardinalityQueryMembershipReplacement_AbortsTheFinalizer()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity selected = world.CreateEntity();
            Entity replacementMember = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, selected, oldParent);
            Hierarchy<Domain>.SetParent(world, replacementMember, oldParent);
            world.AddTag<TopologySelectionTag>(selected);
            QueryHandle query = world.Query(
                world.QueryDefinition()
                    .ReadWrite<Parent<Domain>>()
                    .All<TopologySelectionTag>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            PacketConcurrencyState.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            world.RemoveTag<TopologySelectionTag>(selected);
            world.AddTag<TopologySelectionTag>(replacementMember);
            releasePacket.Set();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => transaction.Handle.Complete());

            Assert.Contains("membership", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, selected));
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, replacementMember));
        });
    }

    [Fact]
    public void StableTopologyProof_RejectsARepeatedPersistentChunkRange()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new StableQueryPartitionProof(
            [
                new StableQueryPacketRange(1, 0, 1, 1),
                new StableQueryPacketRange(2, 0, 1, 1),
                new StableQueryPacketRange(1, 0, 1, 1),
            ]));

        Assert.Contains("reappear", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void StableTopologyProof_RejectsPacketGapsAndOverlaps(int secondStart)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new StableQueryPartitionProof(
            [
                new StableQueryPacketRange(1, 0, 2, 5),
                new StableQueryPacketRange(1, secondStart, 2, 5),
            ]));

        Assert.Contains("contiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StableTopologyProof_RejectsIncompleteChunkTail()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new StableQueryPartitionProof(
            [
                new StableQueryPacketRange(1, 0, 2, 3),
            ]));

        Assert.Contains("cover", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StableTopologyProof_RejectsInconsistentChunkRowCount()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new StableQueryPartitionProof(
            [
                new StableQueryPacketRange(1, 0, 2, 4),
                new StableQueryPacketRange(1, 2, 2, 5),
            ]));

        Assert.Contains("agree", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StableTopologyProof_RejectsTotalRowOffsetOverflow()
    {
        Assert.Throws<OverflowException>(() => new StableQueryPartitionProof(
        [
            new StableQueryPacketRange(1, 0, int.MaxValue, int.MaxValue),
            new StableQueryPacketRange(2, 0, 1, 1),
        ]));
    }

    [Fact]
    public void StableTopologyProof_DerivesEveryStagingOffsetAndRejectsSelfOverlapClaim()
    {
        var proof = new StableQueryPartitionProof(
        [
            new StableQueryPacketRange(1, 0, 2, 5),
            new StableQueryPacketRange(1, 2, 3, 5),
            new StableQueryPacketRange(2, 0, 1, 1),
        ]);

        Assert.Equal(0, proof.GetRowOffset(0));
        Assert.Equal(2, proof.GetRowOffset(1));
        Assert.Equal(5, proof.GetRowOffset(2));
        Assert.Equal(6, proof.TotalRowCount);
        Assert.False(proof.ProvesNonOverlap(0, 0));
    }

    [Fact]
    public void ParentTopologyStage_RequiresExactProofCoverageForEveryDetachedArray()
    {
        var proof = new StableQueryPartitionProof(
        [
            new StableQueryPacketRange(1, 0, 2, 2),
        ]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new ParentTopologyStage<Domain>(
                new World(),
                default,
                new Entity[2],
                new Parent<Domain>[1],
                proof,
                lastSystemVersion: 0));

        Assert.Contains("exactly cover", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParentTopologyStage_RetainsOneCompleteParentBackingAndSparsePacketEdits()
    {
        using var world = new World();
        Entity original = world.CreateEntity();
        Entity replacement = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        var captured = new[]
        {
            new Parent<Domain>(original),
            new Parent<Domain>(original),
        };
        var proof = new StableQueryPartitionProof(
        [
            new StableQueryPacketRange(1, 0, 1, 2),
            new StableQueryPacketRange(1, 1, 1, 2),
        ]);
        var stage = new ParentTopologyStage<Domain>(
            world,
            default,
            [first, second],
            captured,
            proof,
            lastSystemVersion: 0);

        FieldInfo[] completeParentBackings = typeof(ParentTopologyStage<Domain>)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(Parent<Domain>[]))
            .ToArray();
        FieldInfo backing = Assert.Single(completeParentBackings);
        Assert.Equal("_capturedParents", backing.Name);
        Assert.Same(captured, backing.GetValue(stage));
        Assert.Null(typeof(ParentTopologyStage<Domain>).GetProperty(
            "Values",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(ParentTopologyStage<Domain>).GetProperty(
            "OriginalValues",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.Throws<InvalidOperationException>(() => stage.RequirePacketEdits(0));
        stage.PublishPacketEdits(0, [new ParentTopologyEdit(0, replacement)]);
        stage.PublishPacketEdits(1, Array.Empty<ParentTopologyEdit>());

        Assert.True(stage.HasChanges());
        ReadOnlySpan<ParentTopologyEdit> firstEdits = stage.RequirePacketEdits(0);
        Assert.Equal(1, firstEdits.Length);
        Assert.Equal(replacement, firstEdits[0].Replacement);
        Assert.Equal(0, stage.RequirePacketEdits(1).Length);
        for (int index = 0; index < stage.CapturedParents.Length; index++)
            Assert.Equal(original, stage.CapturedParents[index].Value);
        Assert.Throws<InvalidOperationException>(
            () => stage.PublishPacketEdits(0, Array.Empty<ParentTopologyEdit>()));
    }

    [Fact]
    public void ParentTopologyStage_RejectsNonCanonicalOrCrossPacketSparseEdits()
    {
        using var world = new World();
        Entity replacement = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        var proof = new StableQueryPartitionProof(
        [
            new StableQueryPacketRange(1, 0, 2, 2),
        ]);
        var stage = new ParentTopologyStage<Domain>(
            world,
            default,
            [first, second],
            new Parent<Domain>[2],
            proof,
            lastSystemVersion: 0);

        Assert.Throws<InvalidOperationException>(() =>
            stage.PublishPacketEdits(0, [new ParentTopologyEdit(-1, replacement)]));
        Assert.Throws<InvalidOperationException>(() =>
            stage.PublishPacketEdits(0, [new ParentTopologyEdit(2, replacement)]));
        Assert.Throws<InvalidOperationException>(() => stage.PublishPacketEdits(
            0,
            [
                new ParentTopologyEdit(1, replacement),
                new ParentTopologyEdit(0, replacement),
            ]));
        Assert.Throws<InvalidOperationException>(() => stage.PublishPacketEdits(
            0,
            [
                new ParentTopologyEdit(0, replacement),
                new ParentTopologyEdit(0, replacement),
            ]));

        stage.PublishPacketEdits(
            0,
            [
                new ParentTopologyEdit(0, replacement),
                new ParentTopologyEdit(1, replacement),
            ]);
        Assert.Equal(2, stage.RequirePacketEdits(0).Length);
    }

    [Fact]
    public void StableTopologyCapture_MaximumPacketSizeDoesNotOverflowCeilingDivision()
    {
        using var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Hierarchy<Domain>.SetParent(world, first, parent);
        Hierarchy<Domain>.SetParent(world, second, parent);
        QueryHandle query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<Domain>>());

        ParentTopologyStage<Domain> stage = TopologyStablePacketCapture.Capture<Domain>(
            world,
            query,
            rowsPerPacket: int.MaxValue,
            lastSystemVersion: 0);

        Assert.Equal(1, stage.PacketCount);
        Assert.Equal(2, stage.Proof.TotalRowCount);
    }

    [Fact]
    public void Propagation_DerivesDisjointRoots_VisitsOrganizationNodes_AndSupportsMixedOrder()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity orderedRoot = CreateValueNode(world);
            Entity orderedChild = CreateValueNode(world);
            Entity organization = world.CreateEntity();
            Entity organizationLeaf = CreateValueNode(world);
            Entity unorderedRoot = CreateValueNode(world);
            Entity unorderedFirst = CreateValueNode(world);
            Entity unorderedSecond = CreateValueNode(world);

            Hierarchy<Domain>.SetChildOrderPolicy(
                world,
                orderedRoot,
                ChildOrderPolicy.Ordered);
            Hierarchy<Domain>.SetParent(world, orderedChild, orderedRoot, insertIndex: 0);
            Hierarchy<Domain>.SetParent(world, organization, orderedRoot, insertIndex: 1);
            Hierarchy<Domain>.SetParent(world, organizationLeaf, organization);
            Hierarchy<Domain>.SetParent(world, unorderedFirst, unorderedRoot);
            Hierarchy<Domain>.SetParent(world, unorderedSecond, unorderedRoot);

            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            using var rootsArrived = new CountdownEvent(2);
            using var releaseRoots = new ManualResetEventSlim();
            PropagationTestState.Configure(rootsArrived, releaseRoots);
            Entity[] dirty =
            [
                organizationLeaf,
                orderedChild,
                orderedRoot,
                unorderedSecond,
                unorderedRoot,
                orderedRoot,
            ];
            JobResourceAccess[] accesses =
            [
                ComponentJobAccess<PropagationValue>.Write(world),
            ];

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                dirty,
                new RecordingPropagationJob<Domain>(),
                maintenance,
                accesses,
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));

            using var writerStarted = new ManualResetEventSlim();
            JobHandle interveningWriter = default;
            try
            {
                Assert.True(rootsArrived.Wait(TimeSpan.FromSeconds(3)));
                interveningWriter = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                    world,
                    new SignalJob(writerStarted));

                // The planning owner keeps topology-read and Parent-read until every proven
                // subtree packet completes, so a writer cannot enter after proof capture.
                Assert.False(writerStarted.Wait(TimeSpan.FromMilliseconds(150)));
                releaseRoots.Set();
                propagation.Handle.Complete();
                Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(3)));
            }
            finally
            {
                releaseRoots.Set();
                interveningWriter.Complete();
            }

            Assert.Equal(2, propagation.Partition.RootCount);
            Assert.Equal(2, propagation.Partition.PacketCount);
            Assert.Equal(1, propagation.Partition.RootsPerPacket);
            Assert.Equal(
                new[] { orderedRoot, unorderedRoot },
                propagation.Partition.NormalizedRoots.ToArray());
            Assert.Collection(
                propagation.Partition.PacketRanges.ToArray(),
                range =>
                {
                    Assert.Equal(0, range.RootStart);
                    Assert.Equal(1, range.RootCount);
                },
                range =>
                {
                    Assert.Equal(1, range.RootStart);
                    Assert.Equal(1, range.RootCount);
                });
            Assert.True(Volatile.Read(ref PropagationTestState.Maximum) >= 2);
            Assert.True(PropagationTestState.Visited.TryGetValue(organization, out Visit organizationVisit));
            Assert.Equal(orderedRoot, organizationVisit.Root);
            Assert.Equal(1, organizationVisit.Depth);
            Assert.Equal(2, world.Read<PropagationValue>(organizationLeaf).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(orderedChild).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(unorderedFirst).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(unorderedSecond).Depth);
            Assert.Equal(
                new[] { orderedRoot, orderedChild, organization, organizationLeaf },
                PropagationTestState.OrderByRoot[orderedRoot].ToArray());
            Assert.Equal(
                new HashSet<Entity> { unorderedRoot, unorderedFirst, unorderedSecond },
                PropagationTestState.OrderByRoot[unorderedRoot].ToHashSet());
        });
    }

    [Fact]
    public void PropagationSchedule_DoesNotSynchronouslyWaitForMaintenance()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            using var blockerStarted = new ManualResetEventSlim();
            using var releaseBlocker = new ManualResetEventSlim();
            JobHandle blocker = JobSystem.Schedule(
                new SignalBlockingJob(blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(3)));
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world, blocker);
            PropagationTestState.Configure(arrived: null, release: null);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new RecordingPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);

            Assert.False(propagation.Handle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => _ = propagation.Partition);

            releaseBlocker.Set();
            propagation.Handle.Complete();
            Assert.Equal([root], propagation.Partition.NormalizedRoots.ToArray());
            Assert.True(PropagationTestState.Visited.ContainsKey(root));
        });
    }

    [Fact]
    public void Propagation_WritableFamilyUsesOnlyCapturedStableRowRanges()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity selected = CreateValueNode(world);
            Entity unrelated = CreateValueNode(world);
            JobResourceAccess selectedRange =
                ComponentRangeWrite<PropagationValue>(world, selected);
            JobResourceAccess unrelatedRange =
                ComponentRangeWrite<PropagationValue>(world, unrelated);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            using var selectedArrived = new CountdownEvent(1);
            using var releaseSelected = new ManualResetEventSlim();
            using var unrelatedStarted = new ManualResetEventSlim();
            using var overlappingStarted = new ManualResetEventSlim();
            PropagationTestState.Configure(selectedArrived, releaseSelected);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [selected],
                new RecordingPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            JobHandle unrelatedOwner = default;
            JobHandle overlappingOwner = default;
            try
            {
                Assert.True(selectedArrived.Wait(TimeSpan.FromSeconds(3)));

                unrelatedOwner = JobSystem.Schedule(
                    new SignalJob(unrelatedStarted),
                    [unrelatedRange, RelationshipJobAccess.TopologyRead(world)]);
                Assert.True(unrelatedStarted.Wait(TimeSpan.FromSeconds(3)));
                unrelatedOwner.Complete();

                overlappingOwner = JobSystem.Schedule(
                    new SignalJob(overlappingStarted),
                    [selectedRange, RelationshipJobAccess.TopologyRead(world)]);
                Assert.False(overlappingStarted.Wait(TimeSpan.FromMilliseconds(150)));

                releaseSelected.Set();
                propagation.Handle.Complete();
                overlappingOwner.Complete();
                Assert.True(overlappingStarted.IsSet);
            }
            finally
            {
                releaseSelected.Set();
                propagation.Handle.Complete();
                unrelatedOwner.Complete();
                overlappingOwner.Complete();
            }
        });
    }

    [Fact]
    public void Propagation_WritableFamilyOmitsCapturedRowsAndAncestorsWithoutTheComponent()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity missingAncestor = world.CreateEntity();
            Entity root = CreateValueNode(world);
            Entity presentChild = CreateValueNode(world);
            Entity missingChild = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, root, missingAncestor);
            Hierarchy<Domain>.SetParent(world, presentChild, root);
            Hierarchy<Domain>.SetParent(world, missingChild, root);
            JobResourceAccess presentRange =
                ComponentRangeWrite<PropagationValue>(world, presentChild);
            JobResourceAccess missingRange =
                ComponentRangeWrite<PropagationValue>(world, missingChild);
            JobResourceAccess missingAncestorRange =
                ComponentRangeWrite<PropagationValue>(world, missingAncestor);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            using var rootArrived = new CountdownEvent(1);
            using var releaseRoot = new ManualResetEventSlim();
            using var missingStarted = new ManualResetEventSlim();
            using var missingAncestorStarted = new ManualResetEventSlim();
            using var presentStarted = new ManualResetEventSlim();
            PropagationTestState.Configure(rootArrived, releaseRoot);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new RecordingPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            JobHandle missingOwner = default;
            JobHandle missingAncestorOwner = default;
            JobHandle presentOwner = default;
            try
            {
                Assert.True(rootArrived.Wait(TimeSpan.FromSeconds(3)));

                missingOwner = JobSystem.Schedule(
                    new SignalJob(missingStarted),
                    [missingRange, RelationshipJobAccess.TopologyRead(world)]);
                Assert.True(missingStarted.Wait(TimeSpan.FromSeconds(3)));
                missingOwner.Complete();

                missingAncestorOwner = JobSystem.Schedule(
                    new SignalJob(missingAncestorStarted),
                    [missingAncestorRange, RelationshipJobAccess.TopologyRead(world)]);
                Assert.True(missingAncestorStarted.Wait(TimeSpan.FromSeconds(3)));
                missingAncestorOwner.Complete();

                presentOwner = JobSystem.Schedule(
                    new SignalJob(presentStarted),
                    [presentRange, RelationshipJobAccess.TopologyRead(world)]);
                Assert.False(presentStarted.Wait(TimeSpan.FromMilliseconds(150)));

                releaseRoot.Set();
                propagation.Handle.Complete();
                presentOwner.Complete();
                Assert.True(presentStarted.IsSet);
            }
            finally
            {
                releaseRoot.Set();
                propagation.Handle.Complete();
                missingOwner.Complete();
                missingAncestorOwner.Complete();
                presentOwner.Complete();
            }

            Assert.False(world.Has<PropagationValue>(missingAncestor));
            Assert.False(world.Has<PropagationValue>(missingChild));
            Assert.Equal(1, world.Read<PropagationValue>(presentChild).Depth);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Propagation_MissingWritableComponentReportsTheComponentErrorBeforeRangeSafety(
        bool write)
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity();
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new MissingComponentAccessJob<Domain>(write),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => propagation.Handle.Complete());

            Assert.Contains("does not have component", error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(PropagationValue), error.Message, StringComparison.Ordinal);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_WritableFamilyAdmitsExternalAncestorsAsReadOnlyRanges()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity ancestor = CreateValueNode(world);
            Entity root = CreateValueNode(world);
            Entity child = CreateValueNode(world);
            world.Replace(ancestor, new PropagationValue { Depth = 73 });
            Hierarchy<Domain>.SetParent(world, root, ancestor);
            Hierarchy<Domain>.SetParent(world, child, root);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new CopyAncestorValueJob<Domain>(ancestor),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            propagation.Handle.Complete();

            Assert.Equal(73, world.Read<PropagationValue>(root).Depth);
            Assert.Equal(73, world.Read<PropagationValue>(child).Depth);
            Assert.Equal(73, world.Read<PropagationValue>(ancestor).Depth);
        });
    }

    [Fact]
    public void Propagation_UsesOneExecutionVersionForEveryPacket()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity[] roots = Enumerable.Range(0, 12)
                .Select(_ => CreateValueNode(world))
                .ToArray();
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            VersionedPropagationJob<Domain>.Reset();

            HierarchyPropagation propagation = default!;
            try
            {
                propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                    world,
                    roots,
                    new VersionedPropagationJob<Domain>(),
                    maintenance,
                    [ComponentJobAccess<PropagationValue>.Write(world)],
                    new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
                Assert.True(
                    VersionedPropagationJob<Domain>.Started.Wait(TimeSpan.FromSeconds(5)));

                // The first child work item has completed its first admitted World write. Clock
                // movement before the remaining packet writes must not leak into their row
                // metadata from this logical propagation.
                _ = world.AcquireSystemTick();
                _ = world.AcquireSystemTick();
                _ = world.AcquireSystemTick();
                VersionedPropagationJob<Domain>.Release();
                propagation.Handle.Complete();

                uint[] rowVersions = roots
                    .Select(entity => ComponentRowWriteVersion<PropagationValue>(world, entity))
                    .ToArray();
                uint executionVersion = Assert.Single(rowVersions.Distinct());
                Assert.NotEqual(0u, executionVersion);

                uint[] chunkVersions = roots
                    .Select(entity => ComponentChunkChangeVersion<PropagationValue>(world, entity))
                    .Distinct()
                    .ToArray();
                Assert.All(chunkVersions, version => Assert.Equal(executionVersion, version));
            }
            finally
            {
                VersionedPropagationJob<Domain>.Release();
                if (propagation is not null && !propagation.Handle.IsCompleted)
                    propagation.Handle.Complete();
            }
        });
    }

    [Fact]
    public void Propagation_ReadOnlyCallbacksDoNotAcquireAnExecutionVersion()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, root);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            CountingReadOnlyPropagationJob<Domain>.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new CountingReadOnlyPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Read(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(2, CountingReadOnlyPropagationJob<Domain>.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_UnusedWriteCapabilityDoesNotAcquireAnExecutionVersion()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, root);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            CountingNoopPropagationJob<Domain>.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new CountingNoopPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(2, CountingNoopPropagationJob<Domain>.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_EmptyNormalizedRootsDoNotAcquireAnExecutionVersion()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            CountingNoopPropagationJob<Domain>.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [],
                new CountingNoopPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(0, propagation.Partition.PacketCount);
            Assert.Equal(0, CountingNoopPropagationJob<Domain>.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_RejectsCrossRootReadFromAWritableComponentFamily()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity firstRoot = CreateValueNode(world);
            Entity secondRoot = CreateValueNode(world);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [firstRoot, secondRoot],
                new CrossRootReadJob<Domain>(firstRoot, secondRoot),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => propagation.Handle.Complete());

            Assert.Contains("ancestor", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Propagation_UsesExplicitMaintenanceDependencyForFreshChildren()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newOrganizationParent = world.CreateEntity();
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, oldParent);

            JobHandle writer = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, newOrganizationParent));
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world, writer);
            PropagationTestState.Configure(arrived: null, release: null);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [newOrganizationParent],
                new RecordingPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            propagation.Handle.Complete();

            Assert.Equal(newOrganizationParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, newOrganizationParent).ToArray());
            Assert.True(PropagationTestState.Visited.ContainsKey(newOrganizationParent));
            Assert.True(PropagationTestState.Visited.ContainsKey(child));
            Assert.Equal(1, world.Read<PropagationValue>(child).Depth);
        });
    }

    [Fact]
    public void Propagation_RejectsMissingWrongWorldAndOverlappingProofEvidence()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            var otherWorld = new World();
            Entity root = world.CreateEntity();
            Entity otherRoot = otherWorld.CreateEntity();
            var job = new NoopPropagationJob<Domain>();

            Assert.Throws<InvalidOperationException>(
                () => HierarchyPropagationAdapter<Domain>.Schedule(
                    world,
                    [root],
                    job,
                    default));

            HierarchyMaintenanceDependency<Domain> wrongWorld =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(otherWorld);
            Assert.Throws<InvalidOperationException>(
                () => HierarchyPropagationAdapter<Domain>.Schedule(
                    world,
                    [root],
                    job,
                    wrongWorld));
            wrongWorld.Handle.Complete();

            Assert.Throws<InvalidOperationException>(
                () => new HierarchyPropagationPartitionProof(
                    [root, root],
                    [new HierarchyPropagationPacketRange(0, 2)],
                    rootsPerPacket: 2,
                    hierarchyFingerprint: 0,
                    inverseRevision: 1,
                    topologyRevision: 1));

            _ = otherRoot;
        });
    }

    [Fact]
    public void PropagationFingerprint_IncludesParentAndDepthForEqualDfsEntityOrder()
    {
        WithJobRuntime(() =>
        {
            var flat = new World();
            Entity flatRoot = flat.CreateEntity();
            Entity flatFirst = flat.CreateEntity();
            Entity flatSecond = flat.CreateEntity();
            Hierarchy<Domain>.SetParent(flat, flatFirst, flatRoot);
            Hierarchy<Domain>.SetParent(flat, flatSecond, flatRoot);

            var chain = new World();
            Entity chainRoot = chain.CreateEntity();
            Entity chainFirst = chain.CreateEntity();
            Entity chainSecond = chain.CreateEntity();
            Hierarchy<Domain>.SetParent(chain, chainFirst, chainRoot);
            Hierarchy<Domain>.SetParent(chain, chainSecond, chainFirst);

            HierarchyMaintenanceDependency<Domain> flatMaintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(flat);
            HierarchyMaintenanceDependency<Domain> chainMaintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(chain);
            var job = new NoopPropagationJob<Domain>();
            HierarchyPropagation flatPropagation = HierarchyPropagationAdapter<Domain>.Schedule(
                flat,
                [flatRoot],
                job,
                flatMaintenance);
            HierarchyPropagation chainPropagation = HierarchyPropagationAdapter<Domain>.Schedule(
                chain,
                [chainRoot],
                job,
                chainMaintenance);

            flatPropagation.Handle.Complete();
            chainPropagation.Handle.Complete();

            Assert.Equal(
                flatPropagation.Partition.NormalizedRoots[0].Index,
                chainPropagation.Partition.NormalizedRoots[0].Index);
            Assert.NotEqual(
                flatPropagation.Partition.Fingerprint,
                chainPropagation.Partition.Fingerprint);
        });
    }

    [Fact]
    public void CompletedMaintenanceToken_BecomesStaleAfterDeferredReparent()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);

            HierarchyMaintenanceDependency<Domain> stale =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            stale.Handle.Complete();
            Hierarchy<Domain>.SetParentDeferred(world, child, newParent);

            var job = new NoopPropagationJob<Domain>();
            HierarchyPropagation rejected = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [newParent],
                job,
                stale);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => rejected.Handle.Complete());
            Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);

            HierarchyMaintenanceDependency<Domain> fresh =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            HierarchyPropagation accepted = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [newParent],
                job,
                fresh);
            accepted.Handle.Complete();

            Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, newParent).ToArray());
        });
    }

    [Fact]
    public void Propagation_RejectsManagedComponentAliasesBeforeAnyPacketWrites()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity();
            world.Add(root, new PropagationValue { Depth = 7 });
            world.Add(root, new ManagedPropagationValue { Text = "shared" });

            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
        JobResourceAccess[] accesses = new JobResourceAccess[2];
            accesses[0] = ComponentJobAccess<PropagationValue>.Write(world);
            accesses[1] = WorldStorageJobResources.Read(
                world,
                new WorldStorageResourceKey(
                    WorldStorageKind.Table,
                    ComponentMetadata<ManagedPropagationValue>.Id));
            var job = new WriteThenManagedReadJob<Domain>();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => HierarchyPropagationAdapter<Domain>.Schedule(
                    world,
                    [root],
                    job,
                    maintenance,
                    accesses));

            Assert.Contains("managed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(7, world.Read<PropagationValue>(root).Depth);
        });
    }

    [Fact]
    public void Propagation_UnrelatedComponentHookDoesNotDisableWritablePackets()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            world.Hooks<UnrelatedHookValue>().OnReplace(IgnoreUnrelatedHook);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new SetPropagationValueJob<Domain>(42),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            propagation.Handle.Complete();

            Assert.Equal(42, world.Read<PropagationValue>(root).Depth);
        });
    }

    [Fact]
    public void Propagation_MatchingIrrelevantHookEventsDoNotDisableWritablePackets()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            world.Hooks<PropagationValue>()
                .OnAdd(IgnorePropagationHook)
                .OnRemove(IgnorePropagationHook);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new SetPropagationValueJob<Domain>(43),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            propagation.Handle.Complete();

            Assert.Equal(43, world.Read<PropagationValue>(root).Depth);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Propagation_MatchingValueReplaceHookIsRejectedBeforeAnyPacketWrite(
        bool insertHook)
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity(new PropagationValue { Depth = 7 });
            ComponentHooks<PropagationValue> hooks = world.Hooks<PropagationValue>();
            if (insertHook)
                hooks.OnInsert(UnexpectedPropagationHook);
            else
                hooks.OnReplace(UnexpectedPropagationHook);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new SetPropagationValueJob<Domain>(42),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => propagation.Handle.Complete());

            Assert.Contains("callbacks", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(7, world.Read<PropagationValue>(root).Depth);
        });
    }

    [Fact]
    public async Task HookBindingWaitsForActivePropagationTopologyReaders()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            var world = new World();
            Entity root = CreateValueNode(world);
            using var arrived = new CountdownEvent(1);
            using var release = new ManualResetEventSlim();
            PropagationTestState.Configure(arrived, release);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new RecordingPropagationJob<Domain>(),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);

            Assert.True(arrived.Wait(TimeSpan.FromSeconds(3)));
            Task binding = Task.Run(() =>
                world.Hooks<UnrelatedHookValue>().OnReplace(IgnoreUnrelatedHook));
            Task first = await Task.WhenAny(binding, Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(binding, first);

            release.Set();
            propagation.Handle.Complete();
            await binding;
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private static Entity CreateValueNode(World world)
    {
        Entity entity = world.CreateEntity();
        world.Add(entity, new PropagationValue());
        return entity;
    }

    private static JobResourceAccess ComponentRangeWrite<T>(World world, Entity entity)
        where T : struct, IComponent
    {
        long address = StableRowAddress(world, entity);
        return WorldStorageJobResources.Write(
            world,
            new WorldStorageResourceKey(
                WorldStorageKind.Table,
                ComponentMetadata<T>.Id),
            address,
            length: 1);
    }

    private static long StableRowAddress(World world, Entity entity)
    {
        var record = world.ActiveStructureRoot.Entities.ReadRow(entity);
        var chunk = record.Chunk!;
        var range = new StableQueryPacketRange(
            chunk.PersistentIdentity,
            record.RowInChunk,
            rowCount: 1,
            chunk.Count);
        return StableQueryPacketAddress.Address(in range);
    }

    private static uint ComponentRowWriteVersion<T>(World world, Entity entity)
        where T : struct, IComponent
    {
        var record = world.ActiveStructureRoot.Entities.ReadRow(entity);
        int column = record.Archetype!.Column(ComponentMetadata<T>.Id);
        return record.Chunk!.WriteVersionRows(column)[record.RowInChunk];
    }

    private static uint ComponentChunkChangeVersion<T>(World world, Entity entity)
        where T : struct, IComponent
    {
        var record = world.ActiveStructureRoot.Entities.ReadRow(entity);
        int column = record.Archetype!.Column(ComponentMetadata<T>.Id);
        return record.Chunk!.ChangeVersions[column];
    }

    private static void WithJobRuntime(Action action)
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            action();
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private static class PacketConcurrencyState
    {
        private static CountdownEvent? s_arrived;
        private static ManualResetEventSlim? s_release;

        internal static void Configure(CountdownEvent arrived, ManualResetEventSlim release)
        {
            s_arrived = arrived;
            s_release = release;
            Active = 0;
            Maximum = 0;
        }

        internal static int Active;
        internal static int Maximum;

        internal static void Enter()
        {
            int active = Interlocked.Increment(ref Active);
            UpdateMaximum(ref Maximum, active);
            s_arrived!.Signal();
            if (!s_release!.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Parallel packet test release timed out.");
        }

        internal static void Exit()
        {
            Interlocked.Decrement(ref Active);
        }
    }

    private static class ParentObservationState
    {
        internal static int Matches;

        internal static void Reset() => Matches = 0;
    }

    private static class DependencyFaultState
    {
        internal static int PacketExecutions;
    }

    private readonly struct FaultingDependencyJob : IJob
    {
        public void Execute() =>
            throw new InvalidOperationException("dependency fault");
    }

    private readonly struct CountingNoopParentPacketJob<TDomain> :
        IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            _ = entities;
            _ = parents;
            Interlocked.Increment(ref DependencyFaultState.PacketExecutions);
        }
    }

    private readonly struct BlockingParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _parent;

        internal BlockingParentPacketJob(Entity parent)
        {
            _parent = parent;
        }

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            Assert.Single(entities.ToArray());
            parents[0] = new Parent<TDomain>(_parent);
            PacketConcurrencyState.Enter();
            PacketConcurrencyState.Exit();
        }
    }

    private readonly struct BlockingNoopParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            _ = entities;
            _ = parents;
            PacketConcurrencyState.Enter();
            PacketConcurrencyState.Exit();
        }
    }

    private readonly struct FaultingParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _parent;

        internal FaultingParentPacketJob(Entity parent)
        {
            _parent = parent;
        }

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = entities;
            parents[0] = new Parent<TDomain>(_parent);
            if (packet.PacketIndex == 0)
                throw new InvalidOperationException("packet fault");
        }
    }

    private readonly struct ParentCyclePacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _first;
        private readonly Entity _second;

        internal ParentCyclePacketJob(Entity first, Entity second)
        {
            _first = first;
            _second = second;
        }

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i] == _first)
                    parents[i] = new Parent<TDomain>(_second);
                else if (entities[i] == _second)
                    parents[i] = new Parent<TDomain>(_first);
            }
        }
    }

    private readonly struct ObserveAndReplaceParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _expected;
        private readonly Entity _replacement;

        internal ObserveAndReplaceParentPacketJob(
            Entity expected,
            Entity replacement)
        {
            _expected = expected;
            _replacement = replacement;
        }

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            _ = entities;
            for (int i = 0; i < parents.Length; i++)
            {
                if (parents[i].Value != _expected)
                {
                    throw new InvalidOperationException(
                        "Topology capture did not observe the dependency-final Parent image.");
                }
                Interlocked.Increment(ref ParentObservationState.Matches);
                parents[i] = new Parent<TDomain>(_replacement);
            }
        }
    }

    private readonly struct BlockingDeferredParentJob<TDomain> : IJob
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingDeferredParentJob(
            World world,
            Entity child,
            Entity parent,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _child = child;
            _parent = parent;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Topology dependency release timed out.");
            HierarchyJobAccess<TDomain>.SetParentDeferred(_world, _child, _parent);
        }
    }

    private static class PropagationTestState
    {
        private static CountdownEvent? s_arrived;
        private static ManualResetEventSlim? s_release;

        internal static void Configure(
            CountdownEvent? arrived,
            ManualResetEventSlim? release)
        {
            s_arrived = arrived;
            s_release = release;
            Visited = new ConcurrentDictionary<Entity, Visit>();
            OrderByRoot = new ConcurrentDictionary<Entity, ConcurrentQueue<Entity>>();
            Active = 0;
            Maximum = 0;
        }

        internal static ConcurrentDictionary<Entity, Visit> Visited { get; private set; } = new();
        internal static ConcurrentDictionary<Entity, ConcurrentQueue<Entity>> OrderByRoot { get; private set; } = new();
        internal static int Active;
        internal static int Maximum;

        internal static void EnterRoot()
        {
            int active = Interlocked.Increment(ref Active);
            UpdateMaximum(ref Maximum, active);
            s_arrived?.Signal();
            if (s_release is not null && !s_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Parallel propagation test release timed out.");
            Interlocked.Decrement(ref Active);
        }
    }

    private readonly record struct Visit(Entity Root, int Depth, int PacketIndex);

    private readonly struct RecordingPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (context.Depth == 0)
                PropagationTestState.EnterRoot();

            PropagationTestState.Visited[context.Entity] = new Visit(
                context.Root,
                context.Depth,
                context.PacketIndex);
            PropagationTestState.OrderByRoot
                .GetOrAdd(context.Root, static _ => new ConcurrentQueue<Entity>())
                .Enqueue(context.Entity);
            if (context.Has<PropagationValue>())
            {
                context.Write(new PropagationValue
                {
                    Depth = context.Depth,
                });
            }
        }
    }

    private readonly struct DeferredParentJob<TDomain> : IJob
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;

        internal DeferredParentJob(World world, Entity child, Entity parent)
        {
            _world = world;
            _child = child;
            _parent = parent;
        }

        public void Execute()
        {
            HierarchyJobAccess<TDomain>.SetParentDeferred(_world, _child, _parent);
        }
    }

    private readonly struct NoopPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            _ = context.Entity;
        }
    }

    private readonly struct CountingReadOnlyPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private static int s_executionCount;

        internal static int ExecutionCount => Volatile.Read(ref s_executionCount);

        internal static void Reset() => Volatile.Write(ref s_executionCount, 0);

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            _ = context.Read<PropagationValue>();
            Interlocked.Increment(ref s_executionCount);
        }
    }

    private readonly struct CountingNoopPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private static int s_executionCount;

        internal static int ExecutionCount => Volatile.Read(ref s_executionCount);

        internal static void Reset() => Volatile.Write(ref s_executionCount, 0);

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            _ = context.Entity;
            Interlocked.Increment(ref s_executionCount);
        }
    }

    private readonly struct CrossRootReadJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _first;
        private readonly Entity _second;

        internal CrossRootReadJob(Entity first, Entity second)
        {
            _first = first;
            _second = second;
        }

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (context.Entity == _first)
                _ = context.Read<PropagationValue>(_second);
        }
    }

    private readonly struct WriteThenManagedReadJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            context.Write(new PropagationValue { Depth = 99 });
            _ = context.Read<ManagedPropagationValue>();
        }
    }

    private readonly struct SetPropagationValueJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly int _value;

        internal SetPropagationValueJob(int value)
        {
            _value = value;
        }

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            context.Write(new PropagationValue { Depth = _value });
        }
    }

    private readonly struct MissingComponentAccessJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly bool _write;

        internal MissingComponentAccessJob(bool write)
        {
            _write = write;
        }

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (context.Has<PropagationValue>())
            {
                throw new InvalidOperationException(
                    "The missing-component regression node unexpectedly contains PropagationValue.");
            }

            if (_write)
                context.Write(new PropagationValue { Depth = 1 });
            else
                _ = context.Read<PropagationValue>();
        }
    }

    private readonly struct CopyAncestorValueJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _ancestor;

        internal CopyAncestorValueJob(Entity ancestor)
        {
            _ancestor = ancestor;
        }

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            PropagationValue inherited = context.Read<PropagationValue>(_ancestor);
            context.Write(in inherited);
        }
    }

    private readonly struct VersionedPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private static ManualResetEventSlim s_started = new();
        private static ManualResetEventSlim s_release = new();
        private static int s_first;

        internal static ManualResetEventSlim Started => s_started;

        internal static void Reset()
        {
            s_started.Dispose();
            s_release.Dispose();
            s_started = new ManualResetEventSlim();
            s_release = new ManualResetEventSlim();
            Volatile.Write(ref s_first, 0);
        }

        internal static void Release() => s_release.Set();

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (Interlocked.CompareExchange(ref s_first, 1, 0) == 0)
            {
                context.Write(new PropagationValue
                {
                    Depth = checked(100 + context.PacketIndex),
                });
                s_started.Set();
                if (!s_release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Versioned hierarchy propagation was not released.");
                return;
            }

            if (!s_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Versioned hierarchy propagation was not released.");

            context.Write(new PropagationValue
            {
                Depth = checked(100 + context.PacketIndex),
            });
        }
    }

    private readonly struct SignalBlockingJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal SignalBlockingJob(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Blocking test job release timed out.");
        }
    }

    private readonly struct SignalJob : IJob
    {
        private readonly ManualResetEventSlim _started;

        internal SignalJob(ManualResetEventSlim started)
        {
            _started = started;
        }

        public void Execute()
        {
            _started.Set();
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            int previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private struct PropagationValue : IComponent
    {
        public int Depth;
    }

    private struct ManagedPropagationValue : IComponent
    {
        public string? Text;
    }

    private readonly struct UnrelatedHookValue : IComponent;

    private readonly struct TopologySelectionTag : ITag;

    private static void IgnoreUnrelatedHook(
        SomeEngine.ECS.Hooks.DeferredWorld world,
        Entity entity,
        in UnrelatedHookValue value)
    {
        _ = world;
        _ = entity;
        _ = value;
    }

    private static void UnexpectedPropagationHook(
        SomeEngine.ECS.Hooks.DeferredWorld world,
        Entity entity,
        in PropagationValue value)
    {
        _ = world;
        _ = entity;
        _ = value;
        throw new InvalidOperationException("Propagation callback must have been rejected before execution.");
    }

    private static void IgnorePropagationHook(
        SomeEngine.ECS.Hooks.DeferredWorld world,
        Entity entity,
        in PropagationValue value)
    {
        _ = world;
        _ = entity;
        _ = value;
    }

    private readonly struct Domain : IHierarchyDomain;
}
