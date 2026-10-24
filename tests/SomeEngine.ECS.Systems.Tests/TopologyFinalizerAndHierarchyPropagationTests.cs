using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
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

public sealed class TopologyFinalizerAndHierarchyPropagationTests : JobTest
{
    [Fact]
    public void ParentPackets_RunConcurrently_ButPublishOnlyThroughOneFinalizer()
    {
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
            packetData.Value.Configure(arrived, release);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, newParent),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.True(arrived.Wait(TimeSpan.FromSeconds(3)));

            // Both packet spans already contain their edited values, but neither packet can
            // publish canonical Parent or Children.
            Assert.Equal(
                new HashSet<Entity> { first, second },
                Hierarchy<Domain>.GetChildren(world, oldParent).ToArray().ToHashSet());
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, newParent));

            release.Set();
            transaction.Handle.Complete();

            Assert.True(Volatile.Read(ref packetData.Value.Maximum) >= 2);
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
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity parent = world.CreateEntity();
            Entity competingParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            packetData.Value.Configure(packetArrived, releasePacket);
            uint tickBefore = world.CurrentTick;

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingNoopParentPacketJob<Domain>(packetData),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            // A no-op image must not register a late topology writer or validate a stale
            // preimage. The dependency-independent write can finish before the packet returns.
            Hierarchy<Domain>.SetParent(world, child, competingParent);
            releasePacket.Set();
            transaction.Handle.Complete();

            Assert.Equal(tickBefore, world.CurrentTick);
            Assert.Equal(competingParent, Hierarchy<Domain>.GetParent(world, child));
        });
    }

    [Fact]
    public void ParentUpdatesRemainVisibleToChangedQueryAfterInterveningWrites()
    {
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
            packetData.Value.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, stagedParent),
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
            uint interveningVersion = world.CurrentTick;

            releasePacket.Set();
            transaction.Handle.Complete();

            Assert.Equal(stagedParent, Hierarchy<Domain>.GetParent(world, child));

            QueryHandle changedParents = world.Query(
                world.QueryDefinition()
                    .Read<Parent<Domain>>()
                    .Changed<Parent<Domain>>());
            var changed = new List<Entity>();
            world.ExecuteQuery(
                changedParents,
                interveningVersion,
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
            var world = CreateJobWorld();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, oldParent);
            Hierarchy<Domain>.SetParent(world, second, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new FaultingParentPacketJob<Domain>(newParent),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.ThrowsAny<Exception>(() => transaction.Handle.Complete());

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);

            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());

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
            var world = CreateJobWorld();
            Entity root = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, root);
            Hierarchy<Domain>.SetParent(world, second, first);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new ParentCyclePacketJob<Domain>(first, second),
                new TopologyPacketScheduleOptions(rowsPerPacket: 1));

            Assert.ThrowsAny<InvalidOperationException>(() => transaction.Handle.Complete());

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);

            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());

            Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
            Assert.Equal([first], Hierarchy<Domain>.GetChildren(world, root).ToArray());
            Assert.Equal([second], Hierarchy<Domain>.GetChildren(world, first).ToArray());
        });
    }

    [Fact]
    public void FaultedDependency_PermanentlyRejectsPartitionWithoutRunningCapturePackets()
    {
        using var dependencyData = new CallbackData<DependencyFaultState>(new DependencyFaultState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            dependencyData.Value.PacketExecutions = 0;
            JobHandle dependency = Jobs.Schedule(new FaultingDependencyJob());

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new CountingNoopParentPacketJob<Domain>(dependencyData),
                dependency: dependency);

            Assert.ThrowsAny<Exception>(() => transaction.Handle.Complete());
            Assert.Equal(0, Volatile.Read(ref dependencyData.Value.PacketExecutions));

            InvalidOperationException partitionFault = Assert.Throws<InvalidOperationException>(
                () => _ = transaction.Partition);

            InvalidOperationException getPartitionFault = Assert.Throws<InvalidOperationException>(
                () => transaction.GetPartition());

        });
    }

    [Fact]
    public void TopologySchedule_DoesNotBlockAndCapturesTheDependencyFinalImage()
    {
        using var parentData = new CallbackData<ParentObservationState>(new ParentObservationState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity oldParent = world.CreateEntity();
            Entity dependencyParent = world.CreateEntity();
            Entity finalParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var writerStarted = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            parentData.Value.Reset();

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
                new ObserveAndReplaceParentPacketJob<Domain>(parentData,
                    dependencyParent,
                    finalParent),
                dependency: dependency);

            Assert.False(transaction.Handle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => _ = transaction.Partition);

            releaseWriter.Set();
            transaction.Handle.Complete();

            Assert.Equal(1, Volatile.Read(ref parentData.Value.Matches));
            Assert.Equal(finalParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, oldParent));
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, dependencyParent));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, finalParent).ToArray());
        });
    }

    [Fact]
    public void WorldWriterMayRunDuringStaging_AndOptimisticPreimageConflictAbortsFinalizer()
    {
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity oldParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity competingParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            packetData.Value.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, competingParent))
                .Complete();

            releasePacket.Set();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => transaction.Handle.Complete());

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
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
            packetData.Value.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, stagedParent));
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
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity originalParent = world.CreateEntity();
            Entity intermediateParent = world.CreateEntity();
            Entity stagedParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, originalParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().ReadWrite<Parent<Domain>>());
            using var packetArrived = new CountdownEvent(1);
            using var releasePacket = new ManualResetEventSlim();
            packetData.Value.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, stagedParent));
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
        using var packetData = new CallbackData<PacketConcurrencyState>(new PacketConcurrencyState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
            packetData.Value.Configure(packetArrived, releasePacket);

            TopologyFinalization transaction = TopologyPacketFinalizer<Domain>.Schedule(
                world,
                query,
                new BlockingParentPacketJob<Domain>(packetData, stagedParent));
            Assert.True(packetArrived.Wait(TimeSpan.FromSeconds(3)));

            world.RemoveTag<TopologySelectionTag>(selected);
            world.AddTag<TopologySelectionTag>(replacementMember);
            releasePacket.Set();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => transaction.Handle.Complete());

            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, selected));
            Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, replacementMember));
        });
    }

    [Fact]
    public void Propagation_DerivesDisjointRoots_VisitsOrganizationNodes_AndSupportsMixedOrder()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var propagationData = new CallbackData<PropagationTestState>(new PropagationTestState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
        releaseRoots.WaitCallback = () => events.Enqueue("released");
            propagationData.Value.Configure(rootsArrived, releaseRoots);
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
                new RecordingPropagationJob<Domain>(propagationData),
                maintenance,
                accesses,
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));

            using var writerStarted = new ManualResetEventSlim();
        writerStarted.SetCallback = () => events.Enqueue("writerStarted");
            JobHandle interveningWriter = default;
            try
            {
                Assert.True(rootsArrived.Wait(TimeSpan.FromSeconds(3)));
                interveningWriter = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                    world,
                    new SignalJob(writerStarted));

                // The planning owner keeps topology-read and Parent-read until every proven
                // subtree packet completes, so a writer cannot enter after proof capture.

                releaseRoots.Set();
                propagation.Handle.Complete();
                Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(3)));
            }
            finally
            {
                releaseRoots.Set();
                interveningWriter.Complete();
            }

            Assert.True(Volatile.Read(ref propagationData.Value.Maximum) >= 2);
            Assert.True(propagationData.Value.Visited.TryGetValue(organization, out Visit organizationVisit));
            Assert.Equal(orderedRoot, organizationVisit.Root);
            Assert.Equal(1, organizationVisit.Depth);
            Assert.Equal(2, world.Read<PropagationValue>(organizationLeaf).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(orderedChild).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(unorderedFirst).Depth);
            Assert.Equal(1, world.Read<PropagationValue>(unorderedSecond).Depth);
            Assert.Equal(
                new[] { orderedRoot, orderedChild, organization, organizationLeaf },
                propagationData.Value.OrderByRoot[orderedRoot].ToArray());
            Assert.Equal(
                new HashSet<Entity> { unorderedRoot, unorderedFirst, unorderedSecond },
                propagationData.Value.OrderByRoot[unorderedRoot].ToHashSet());
        });

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("writerStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "writerStarted"));
    }

    [Fact]
    public void PropagationSchedule_DoesNotSynchronouslyWaitForMaintenance()
    {
        using var propagationData = new CallbackData<PropagationTestState>(new PropagationTestState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity root = CreateValueNode(world);
            using var blockerStarted = new ManualResetEventSlim();
            using var releaseBlocker = new ManualResetEventSlim();
            JobHandle blocker = Jobs.Schedule(
                new SignalBlockingJob(blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(3)));
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world, blocker);
            propagationData.Value.Configure(arrived: null, release: null);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new RecordingPropagationJob<Domain>(propagationData),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);

            Assert.False(propagation.Handle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => _ = propagation.Partition);

            releaseBlocker.Set();
            propagation.Handle.Complete();
            Assert.True(propagationData.Value.Visited.ContainsKey(root));
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
            var world = CreateJobWorld();
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

            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_WritableFamilyAdmitsExternalAncestorsAsReadOnlyRanges()
    {
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
    public void PropagationUpdatesEveryRootDespiteInterveningClockChanges()
    {
        using var versionedPropagationJobData = new CallbackData<VersionedPropagationData<Domain>>(new VersionedPropagationData<Domain>());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity[] roots = Enumerable.Range(0, 12)
                .Select(_ => CreateValueNode(world))
                .ToArray();
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            versionedPropagationJobData.Value.Reset();

            HierarchyPropagation propagation = default!;
            try
            {
                propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                    world,
                    roots,
                    new VersionedPropagationJob<Domain>(versionedPropagationJobData),
                    maintenance,
                    [ComponentJobAccess<PropagationValue>.Write(world)],
                    new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
                Assert.True(
                    versionedPropagationJobData.Value.Started.Wait(TimeSpan.FromSeconds(5)));

                // The first child work item has completed its first admitted World write. Clock
                // movement before the remaining packet writes must not leak into their row
                // metadata from this logical propagation.
                _ = world.AcquireSystemTick();
                _ = world.AcquireSystemTick();
                _ = world.AcquireSystemTick();
                versionedPropagationJobData.Value.Release();
                propagation.Handle.Complete();

                Assert.All(roots, entity => Assert.True(world.Read<PropagationValue>(entity).Depth >= 100));
            }
            finally
            {
                versionedPropagationJobData.Value.Release();
                if (propagation is not null && !propagation.Handle.IsCompleted)
                    propagation.Handle.Complete();
            }
        });
    }

    [Fact]
    public void Propagation_ReadOnlyCallbacksDoNotAcquireAnExecutionVersion()
    {
        using var countingReadOnlyPropagationJobData = new CallbackData<CountingReadOnlyPropagationData<Domain>>(new CountingReadOnlyPropagationData<Domain>());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity root = CreateValueNode(world);
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, root);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            countingReadOnlyPropagationJobData.Value.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new CountingReadOnlyPropagationJob<Domain>(countingReadOnlyPropagationJobData),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Read(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(2, countingReadOnlyPropagationJobData.Value.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_UnusedWriteCapabilityDoesNotAcquireAnExecutionVersion()
    {
        using var countingNoopPropagationJobData = new CallbackData<CountingNoopPropagationData<Domain>>(new CountingNoopPropagationData<Domain>());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity root = CreateValueNode(world);
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, root);
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            countingNoopPropagationJobData.Value.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [root],
                new CountingNoopPropagationJob<Domain>(countingNoopPropagationJobData),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(2, countingNoopPropagationJobData.Value.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_EmptyNormalizedRootsDoNotAcquireAnExecutionVersion()
    {
        using var countingNoopPropagationJobData = new CallbackData<CountingNoopPropagationData<Domain>>(new CountingNoopPropagationData<Domain>());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
            maintenance.Handle.Complete();
            countingNoopPropagationJobData.Value.Reset();
            uint tickBefore = world.CurrentTick;

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [],
                new CountingNoopPropagationJob<Domain>(countingNoopPropagationJobData),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)],
                new HierarchyPropagationScheduleOptions(rootsPerPacket: 1));
            propagation.Handle.Complete();

            Assert.Equal(0, countingNoopPropagationJobData.Value.ExecutionCount);
            Assert.Equal(tickBefore, world.CurrentTick);
        });
    }

    [Fact]
    public void Propagation_RejectsCrossRootReadFromAWritableComponentFamily()
    {
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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

        });
    }

    [Fact]
    public void Propagation_UsesExplicitMaintenanceDependencyForFreshChildren()
    {
        using var propagationData = new CallbackData<PropagationTestState>(new PropagationTestState());
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            Entity oldParent = world.CreateEntity();
            Entity newOrganizationParent = world.CreateEntity();
            Entity child = CreateValueNode(world);
            Hierarchy<Domain>.SetParent(world, child, oldParent);

            JobHandle writer = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new DeferredParentJob<Domain>(world, child, newOrganizationParent));
            HierarchyMaintenanceDependency<Domain> maintenance =
                HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world, writer);
            propagationData.Value.Configure(arrived: null, release: null);

            HierarchyPropagation propagation = HierarchyPropagationAdapter<Domain>.Schedule(
                world,
                [newOrganizationParent],
                new RecordingPropagationJob<Domain>(propagationData),
                maintenance,
                [ComponentJobAccess<PropagationValue>.Write(world)]);
            propagation.Handle.Complete();

            Assert.Equal(newOrganizationParent, Hierarchy<Domain>.GetParent(world, child));
            Assert.Equal([child], Hierarchy<Domain>.GetChildren(world, newOrganizationParent).ToArray());
            Assert.True(propagationData.Value.Visited.ContainsKey(newOrganizationParent));
            Assert.True(propagationData.Value.Visited.ContainsKey(child));
            Assert.Equal(1, world.Read<PropagationValue>(child).Depth);
        });
    }

    [Fact]
    public void Propagation_RejectsMissingWrongWorldAndOverlappingProofEvidence()
    {
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
            var otherWorld = CreateJobWorld();
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

            _ = otherRoot;
        });
    }

    [Fact]
    public void CompletedMaintenanceToken_BecomesStaleAfterDeferredReparent()
    {
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
    public void TypedPropagationCapabilitiesRejectManagedValuesBeforeWrites()
    {
        var world = CreateJobWorld();
        var root = world.CreateEntity(new PropagationValue { Depth = 7 });
        world.Add(root, new ManagedPropagationValue { Text = "shared" });
        Assert.Throws<InvalidOperationException>(() =>
            ComponentJobAccess<ManagedPropagationValue>.Read(world));
        Assert.Equal(7, world.Read<PropagationValue>(root).Depth);
    }

    [Fact]
    public void Propagation_UnrelatedComponentHookDoesNotDisableWritablePackets()
    {
        WithJobRuntime(() =>
        {
            var world = CreateJobWorld();
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
            var world = CreateJobWorld();
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
            var world = CreateJobWorld();
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

            Assert.Equal(7, world.Read<PropagationValue>(root).Depth);
        });
    }

    [Fact]
    public async Task HookBindingFollowsPropagationCallbackRelease()
    {
        using var data = new CallbackData<PropagationTestState>(new PropagationTestState());
        var world = CreateJobWorld();
        var root = CreateValueNode(world);
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        release.WaitCallback = () => events.Enqueue("callback released");
        data.Value.Configure(arrived, release);
        var maintenance = HierarchyMaintenanceSystem<Domain>.ScheduleDependency(world);
        var propagation = HierarchyPropagationAdapter<Domain>.Schedule(world, [root],
            new RecordingPropagationJob<Domain>(data), maintenance,
            [ComponentJobAccess<PropagationValue>.Write(world)]);
        Task? binding = null;
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            binding = Task.Factory.StartNew(() =>
            {
                attempted.Set();
                world.Hooks<UnrelatedHookValue>().OnReplace(IgnoreUnrelatedHook);
                events.Enqueue("hooks bound");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            propagation.Handle.Complete();
            await binding.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "callback released", "hooks bound" }, events.ToArray());
        }
        finally
        {
            release.Set();
            propagation.Handle.Complete();
            if (binding is not null)
                await binding.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private Entity CreateValueNode(World world)
    {
        Entity entity = world.CreateEntity();
        world.Add(entity, new PropagationValue());
        return entity;
    }

    private void WithJobRuntime(Action action)
    {
        ManagedPayloadPolicy previousPolicy = Jobs.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = Jobs.SafetyMode;
        ConfigureJobs(new JobRuntimeConfig
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
            ConfigureJobs(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private class PacketConcurrencyState
    {
        private CountdownEvent? _arrived;
        private ManualResetEventSlim? _release;

        internal void Configure(CountdownEvent arrived, ManualResetEventSlim release)
        {
            _arrived = arrived;
            _release = release;
            Active = 0;
            Maximum = 0;
        }

        internal int Active;
        internal int Maximum;

        internal void Enter()
        {
            int active = Interlocked.Increment(ref Active);
            UpdateMaximum(ref Maximum, active);
            _arrived!.Signal();
            if (!_release!.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Parallel packet test release timed out.");
        }

        internal void Exit()
        {
            Interlocked.Decrement(ref Active);
        }
    }

    private class ParentObservationState
    {
        internal int Matches;

        internal void Reset() => Matches = 0;
    }

    private class DependencyFaultState
    {
        internal int PacketExecutions;
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
        internal CountingNoopParentPacketJob(CallbackData<DependencyFaultState> data) => _data = data;

        private readonly CallbackData<DependencyFaultState> _data;

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            _ = entities;
            _ = parents;
            Interlocked.Increment(ref _data.Value.PacketExecutions);
        }
    }

    private readonly struct BlockingParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly CallbackData<PacketConcurrencyState> _data;

        private readonly Entity _parent;

        internal BlockingParentPacketJob(CallbackData<PacketConcurrencyState> data, Entity parent)
        {
            _data = data;
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
            _data.Value.Enter();
            _data.Value.Exit();
        }
    }

    private readonly struct BlockingNoopParentPacketJob<TDomain> : IParentTopologyPacketJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        internal BlockingNoopParentPacketJob(CallbackData<PacketConcurrencyState> data) => _data = data;

        private readonly CallbackData<PacketConcurrencyState> _data;

        public void Execute(
            in TopologyPacketContext packet,
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            _ = packet;
            _ = entities;
            _ = parents;
            _data.Value.Enter();
            _data.Value.Exit();
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
        private readonly CallbackData<ParentObservationState> _data;

        private readonly Entity _expected;
        private readonly Entity _replacement;

        internal ObserveAndReplaceParentPacketJob(CallbackData<ParentObservationState> data,
            Entity expected,
            Entity replacement)
        {
            _data = data;
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
                Interlocked.Increment(ref _data.Value.Matches);
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

    private class PropagationTestState
    {
        private CountdownEvent? _arrived;
        private ManualResetEventSlim? _release;

        internal void Configure(
            CountdownEvent? arrived,
            ManualResetEventSlim? release)
        {
            _arrived = arrived;
            _release = release;
            Visited = new ConcurrentDictionary<Entity, Visit>();
            OrderByRoot = new ConcurrentDictionary<Entity, ConcurrentQueue<Entity>>();
            Active = 0;
            Maximum = 0;
        }

        internal ConcurrentDictionary<Entity, Visit> Visited { get; private set; } = new();
        internal ConcurrentDictionary<Entity, ConcurrentQueue<Entity>> OrderByRoot { get; private set; } = new();
        internal int Active;
        internal int Maximum;

        internal void EnterRoot()
        {
            int active = Interlocked.Increment(ref Active);
            UpdateMaximum(ref Maximum, active);
            _arrived?.Signal();
            if (_release is not null && !_release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Parallel propagation test release timed out.");
            Interlocked.Decrement(ref Active);
        }
    }

    private readonly record struct Visit(Entity Root, int Depth, int PacketIndex);

    private readonly struct RecordingPropagationJob<TDomain> : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        internal RecordingPropagationJob(CallbackData<PropagationTestState> data) => _data = data;

        private readonly CallbackData<PropagationTestState> _data;

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (context.Depth == 0)
                _data.Value.EnterRoot();

            _data.Value.Visited[context.Entity] = new Visit(
                context.Root,
                context.Depth,
                context.PacketIndex);
            _data.Value.OrderByRoot
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

    private sealed class CountingReadOnlyPropagationData<TDomain>
        where TDomain : IHierarchyDomain
    {
        private int _executionCount;

        internal int ExecutionCount => Volatile.Read(ref _executionCount);

        internal void Reset() => Volatile.Write(ref _executionCount, 0);

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            _ = context.Read<PropagationValue>();
            Interlocked.Increment(ref _executionCount);
        }
    }

    private readonly struct CountingReadOnlyPropagationJob<TDomain>(CallbackData<CountingReadOnlyPropagationData<TDomain>> data) : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context) => data.Value.Execute(ref context);
    }

    private sealed class CountingNoopPropagationData<TDomain>
        where TDomain : IHierarchyDomain
    {
        private int _executionCount;

        internal int ExecutionCount => Volatile.Read(ref _executionCount);

        internal void Reset() => Volatile.Write(ref _executionCount, 0);

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            _ = context.Entity;
            Interlocked.Increment(ref _executionCount);
        }
    }

    private readonly struct CountingNoopPropagationJob<TDomain>(CallbackData<CountingNoopPropagationData<TDomain>> data) : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context) => data.Value.Execute(ref context);
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

    private sealed class VersionedPropagationData<TDomain> : IDisposable
        where TDomain : IHierarchyDomain
    {
        private ManualResetEventSlim _started = new();
        private ManualResetEventSlim _release = new();
        private int _first;

        internal ManualResetEventSlim Started => _started;

        internal void Reset()
        {
            _started.Dispose();
            _release.Dispose();
            _started = new ManualResetEventSlim();
            _release = new ManualResetEventSlim();
            Volatile.Write(ref _first, 0);
        }

        internal void Release() => _release.Set();

        public void Execute(ref HierarchyPropagationContext<TDomain> context)
        {
            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                context.Write(new PropagationValue
                {
                    Depth = checked(100 + context.PacketIndex),
                });
                _started.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Versioned hierarchy propagation was not released.");
                return;
            }

            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Versioned hierarchy propagation was not released.");

            context.Write(new PropagationValue
            {
                Depth = checked(100 + context.PacketIndex),
            });
        }
        public void Dispose() { _started.Dispose(); _release.Dispose(); }
    }

    private readonly struct VersionedPropagationJob<TDomain>(CallbackData<VersionedPropagationData<TDomain>> data) : IHierarchyPropagationJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        public void Execute(ref HierarchyPropagationContext<TDomain> context) => data.Value.Execute(ref context);
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

    private void IgnoreUnrelatedHook(
        SomeEngine.ECS.Hooks.DeferredWorld world,
        Entity entity,
        in UnrelatedHookValue value)
    {
        _ = world;
        _ = entity;
        _ = value;
    }

    private void UnexpectedPropagationHook(
        SomeEngine.ECS.Hooks.DeferredWorld world,
        Entity entity,
        in PropagationValue value)
    {
        _ = world;
        _ = entity;
        _ = value;
        throw new InvalidOperationException("Propagation callback must have been rejected before execution.");
    }

    private void IgnorePropagationHook(
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
