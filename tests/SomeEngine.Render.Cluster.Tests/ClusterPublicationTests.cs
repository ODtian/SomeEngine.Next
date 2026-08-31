using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPublicationTests
{
    private const int PositionBytes = 3 * sizeof(ushort);
    private const uint VertexStride = 16;
    private const int IndexBytes = 3;
    private const int PageBytes = MeshPageHeader.Size + GPUCluster.SizeInBytes
        + PositionBytes + (int)VertexStride + IndexBytes;
    private const uint PageAllocationBytes = (uint)((PageBytes + 15) & ~15);

    [Fact]
    public async Task RegistrationAndPageResidencyBecomeVisibleOnlyWhenPendingChangesPublish()
    {
        var globalBvh = new byte[checked((int)ClusterBvh.NodeBytes)];
        using var manager = new ClusterMeshes(
            PageAllocationBytes,
            residency: null,
            pageStorage: null,
            bvhStorage: new TestClusterBvhStorage(globalBvh));
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Mesh", Leaf()));
        Mesh handle = registration.Mesh;

        Assert.Equal(1u, registration.PageCount);
        ClusterMeshesSnapshot registered = manager.CaptureSnapshot();
        Assert.Equal(0u, registered.Pages.Resident);
        Assert.Equal(1u, registered.Pages.Missing);
        Assert.Equal(0, registered.PublishedMeshCount);
        Assert.Equal(0, registered.Pages.UncompletedLoads);
        Assert.Equal(PageAllocationBytes, registered.Heap.FreeBytes);
        Assert.False(manager.TryGetPublishedRoot(handle, out _));

        Assert.True(manager.PublishPending());

        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(0u, published.Pages.Resident);
        Assert.Equal(1u, published.Pages.Missing);
        Assert.Equal(1, published.PublishedMeshCount);
        Assert.Equal(0, published.Pages.UncompletedLoads);
        Assert.True(published.ManagerStateRevision > registered.ManagerStateRevision);
        Assert.True(manager.TryGetPublishedRoot(handle, out uint root));
        Assert.Equal(ClusterBvh.PageFaultMarker, ReadChildPointer(globalBvh, root));
        Assert.False(manager.PublishPending());

        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        ClusterMeshesSnapshot staged = manager.CaptureSnapshot();
        Assert.Equal(0u, staged.Pages.Resident);
        Assert.Equal(1u, staged.Pages.Missing);
        Assert.Equal(1, staged.Pages.UncompletedLoads);
        Assert.Equal(0u, staged.Heap.FreeBytes);
        Assert.Equal(PageAllocationBytes, staged.Residency.GpuUsedBytes);
        Assert.True(manager.TryGetPublishedRoot(handle, out uint unchangedRoot));
        Assert.Equal(root, unchangedRoot);
        Assert.Equal(ClusterBvh.PageFaultMarker, ReadChildPointer(globalBvh, root));

        Assert.True(manager.PublishPending());

        ClusterMeshesSnapshot resident = manager.CaptureSnapshot();
        Assert.Equal(1u, resident.Pages.Resident);
        Assert.Equal(0u, resident.Pages.Missing);
        Assert.Equal(0, resident.Pages.UncompletedLoads);
        Assert.Equal(0u, resident.Heap.FreeBytes);
        Assert.Equal(PageAllocationBytes, resident.Residency.GpuUsedBytes);
        Assert.Equal(0u, ReadChildPointer(globalBvh, root));
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task PendingRegistrationsCoalesceIntoOneAtomicPublication()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration firstRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("First", Leaf()));
        Mesh firstHandle = firstRegistration.Mesh;

        ClusterMeshRegistration secondRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Second", Leaf()));
        Mesh secondHandle = secondRegistration.Mesh;

        Assert.Equal(1u, firstRegistration.PageCount);
        Assert.Equal(1u, secondRegistration.PageCount);
        Assert.Equal(
            checked(firstRegistration.FirstPageId + firstRegistration.PageCount),
            secondRegistration.FirstPageId);
        ClusterMeshesSnapshot bothRegistered = manager.CaptureSnapshot();
        Assert.Equal(2u, bothRegistered.Pages.Registered);
        Assert.Equal(0, bothRegistered.PublishedMeshCount);
        Assert.False(manager.TryGetPublishedRoot(firstHandle, out _));
        Assert.False(manager.TryGetPublishedRoot(secondHandle, out _));

        Assert.True(manager.PublishPending());
        Assert.True(manager.TryGetPublishedRoot(firstHandle, out uint firstRoot));
        Assert.True(manager.TryGetPublishedRoot(secondHandle, out uint secondRoot));
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.Equal(2, manager.CaptureSnapshot().PublishedMeshCount);
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task RegistrationCleanupFailurePreservesPrimaryErrorAndQuarantinesDestination()
    {
        var storage = new FailOnceBvhStorage(
            new byte[checked((int)(ClusterBvh.NodeBytes * 4))]);
        using var manager = new ClusterMeshes(
            PageAllocationBytes,
            residency: null,
            pageStorage: null,
            bvhStorage: storage);
        using Mesh first = await ClusterTestAssets.OpenRuntimeMeshAsync(MeshWithBvh("First", Leaf()));
        _ = await manager.AddMeshAsync(first);
        CompletePublication(manager);

        storage.FailNextRelease();
        using Mesh invalid = await ClusterTestAssets.OpenRuntimeMeshAsync(
            MeshWithBvh("Invalid", Leaf(localPage: 1)));
        InvalidOperationException primary = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddMeshAsync(invalid).AsTask());

        Assert.Contains("references local page", primary.Message, StringComparison.Ordinal);
        ClusterCleanupFailure cleanup = Assert.IsType<ClusterCleanupFailure>(
            manager.CaptureSnapshot().LastCleanupFailure);
        Assert.Equal(ClusterCleanupStage.Registration, cleanup.Stage);
        Assert.Contains("Injected BVH release failure", cleanup.Message, StringComparison.Ordinal);

        using Mesh second = await ClusterTestAssets.OpenRuntimeMeshAsync(MeshWithBvh("Second", Leaf()));
        _ = await manager.AddMeshAsync(second);
        CompletePublication(manager);

        Assert.True(manager.TryGetPublishedRoot(second, out uint secondRoot));
        Assert.Equal(2u, secondRoot);
        Assert.Equal(2, manager.CaptureSnapshot().PublishedMeshCount);
    }

    [Fact]
    public async Task PendingChangesCanBeDiscardedByEpochDisposal()
    {
        var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Pending", Leaf()));
        ClusterMeshesSnapshot pending = manager.CaptureSnapshot();
        Assert.Equal(1u, pending.Pages.Registered);
        Assert.Equal(1u, pending.Pages.Missing);
        Assert.Equal(0, pending.PublishedMeshCount);
        Assert.False(manager.TryGetPublishedRoot(registration.Mesh, out _));

        manager.Dispose();
        ClusterMeshesSnapshot terminal = manager.CaptureSnapshot();
        Assert.Equal(ClusterLifecycle.Disposed, terminal.Lifecycle);
        Assert.Equal(0, terminal.PublishedMeshCount);
        Assert.Equal(PageAllocationBytes, terminal.Heap.FreeBytes);
        Assert.Throws<ObjectDisposedException>(() => manager.PublishPending());
    }

    [Fact]
    public async Task DisposalReleasesPublishedResidencyAndExposesAStableTerminalSnapshot()
    {
        var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Mesh", Leaf()));
        Mesh handle = registration.Mesh;
        CompletePublication(manager);
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        CompletePublication(manager);
        ClusterMeshesSnapshot completed = manager.CaptureSnapshot();
        Assert.Equal(ClusterLifecycle.Active, completed.Lifecycle);
        Assert.Equal(1u, completed.Pages.Resident);
        Assert.Equal(PageAllocationBytes, completed.Residency.GpuUsedBytes);

        manager.Dispose();

        ClusterMeshesSnapshot terminal = manager.CaptureSnapshot();
        Assert.Equal(ClusterLifecycle.Disposed, terminal.Lifecycle);
        Assert.Equal(completed.EpochId, terminal.EpochId);
        Assert.True(terminal.ManagerStateRevision > completed.ManagerStateRevision);
        Assert.Equal(default, terminal.Pages);
        Assert.Equal(PageAllocationBytes, terminal.Heap.CapacityBytes);
        Assert.Equal(0u, terminal.Heap.UsedBytes);
        Assert.Equal(PageAllocationBytes, terminal.Heap.FreeBytes);
        Assert.Equal(PageAllocationBytes, terminal.Heap.LargestFreeBlockBytes);
        Assert.Equal(1, terminal.Heap.FreeBlockCount);
        Assert.Equal(0, terminal.Residency.GpuUsedBytes);
        Assert.Equal(0, terminal.PublishedMeshCount);
        Assert.Equal(0, terminal.ActivePageStreams);
        manager.Dispose();
        Assert.Equal(terminal, manager.CaptureSnapshot());

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId).AsTask());
        Assert.Throws<ObjectDisposedException>(() => manager.PublishPending());
        Assert.Throws<ObjectDisposedException>(() => manager.TryGetPublishedRoot(handle, out _));
    }

    [Fact]
    public async Task DisposalCleanupFailureIsReportedAfterTerminalStateAndIsIdempotent()
    {
        var storage = new FailOnceBvhStorage(
            new byte[checked((int)(ClusterBvh.NodeBytes * 2))]);
        var manager = new ClusterMeshes(
            PageAllocationBytes,
            residency: null,
            pageStorage: null,
            bvhStorage: storage);
        await manager.AddAuthoredMeshAsync(MeshWithBvh("DisposalFailure", Leaf()));
        CompletePublication(manager);
        storage.FailNextRelease();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(manager.Dispose);

        Assert.Contains("cleanup failures", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Injected BVH release failure", failure.InnerException!.Message, StringComparison.Ordinal);
        ClusterMeshesSnapshot terminal = manager.CaptureSnapshot();
        Assert.Equal(ClusterLifecycle.Disposed, terminal.Lifecycle);
        ClusterCleanupFailure cleanup = Assert.IsType<ClusterCleanupFailure>(terminal.LastCleanupFailure);
        Assert.Equal(ClusterCleanupStage.Disposal, cleanup.Stage);
        Assert.Contains("Injected BVH release failure", cleanup.Message, StringComparison.Ordinal);
        Assert.Null(Record.Exception(manager.Dispose));
        Assert.Equal(terminal, manager.CaptureSnapshot());
    }

    [Fact]
    public async Task ConcurrentPublishCommitsPendingChangesExactlyOnce()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("ConcurrentCompletion", Leaf()));
        Mesh handle = registration.Mesh;
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));

        int publishedCount = 0;
        Parallel.For(0, 32, _ =>
        {
            if (manager.PublishPending())
                Interlocked.Increment(ref publishedCount);
        });

        Assert.Equal(1, publishedCount);
        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(1u, published.Pages.Resident);
        Assert.Equal(0u, published.Pages.Missing);
        Assert.Equal(PageAllocationBytes, published.Residency.GpuUsedBytes);
        Assert.Equal(1, published.PublishedMeshCount);
        Assert.True(manager.TryGetPublishedRoot(handle, out _));
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task EvictedAllocationIsRetiredOnlyWhenPublishedAndReloadReadsDirectlyIntoFinalOwner()
    {
        var pageStorageBytes = new byte[PageAllocationBytes];
        using var manager = new ClusterMeshes(
            PageAllocationBytes,
            residency: null,
            pageStorage: new TestClusterPageStorage(pageStorageBytes));
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            MeshWithBvh("Mesh", Leaf()),
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh);
        Mesh handle = registration.Mesh;
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(handle, out _));

        uint pageId = registration.FirstPageId;
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, pageId));
        CompletePublication(manager);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.FreeBytes);

        Assert.True(manager.EvictPage(pageId));
        ClusterMeshesSnapshot evictionPending = manager.CaptureSnapshot();
        Assert.Equal(1u, evictionPending.Pages.Resident);
        Assert.Equal(1, evictionPending.Pages.UncompletedEvictions);
        Assert.Equal(0u, evictionPending.Heap.FreeBytes);

        Assert.True(manager.PublishPending());
        ClusterMeshesSnapshot evicted = manager.CaptureSnapshot();
        Assert.Equal(0u, evicted.Pages.Resident);
        Assert.Equal(0, evicted.Pages.UncompletedEvictions);
        Assert.Equal(PageAllocationBytes, evicted.Heap.FreeBytes);

        bool usedFinalDestination = false;
        controlled.Source.AfterRead = (_, destination) =>
        {
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment));
            Assert.Same(pageStorageBytes, segment.Array);
            usedFinalDestination = true;
        };
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, pageId));

        Assert.True(usedFinalDestination);
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Resident);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.FreeBytes);

        Assert.True(manager.PublishPending());
        ClusterMeshesSnapshot reloaded = manager.CaptureSnapshot();
        Assert.Equal(1u, reloaded.Pages.Resident);
        Assert.Equal(0, reloaded.Pages.UncompletedLoads);
        Assert.Equal(PageAllocationBytes, reloaded.Residency.GpuUsedBytes);
    }

    [Fact]
    public async Task UnknownPageOrAuthenticatedPayloadFailureCannotMutateHeapOrPublicationState()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            MeshWithBvh("Mesh", Leaf()),
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh);
        CompletePublication(manager);
        uint pageId = registration.FirstPageId;
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, pageId));
        CompletePublication(manager);
        Assert.True(manager.EvictPage(pageId));
        CompletePublication(manager);

        ClusterMeshesSnapshot checkpoint = manager.CaptureSnapshot();

        Assert.Equal(PageLoadResult.UnknownPage, await ClusterTestAssets.LoadPageAsync(manager, 99));
        controlled.Source.AfterRead = static (_, destination) => destination.Span[^1] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ClusterTestAssets.LoadPageAsync(manager, pageId).AsTask());

        AssertEquivalentExceptRevision(checkpoint, manager.CaptureSnapshot());
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task ThrowingPageSourceCannotLeakAHeapAllocation()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            MeshWithBvh("Mesh", Leaf()),
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh);
        CompletePublication(manager);
        controlled.Source.BeforeRead = static (_, _, _) =>
            ValueTask.FromException(new InvalidOperationException("Injected page-source failure."));
        ClusterMeshesSnapshot checkpoint = manager.CaptureSnapshot();

        ClusterPageSourceException error = await Assert.ThrowsAsync<ClusterPageSourceException>(
            () => ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId).AsTask());
        Assert.IsType<InvalidOperationException>(error.InnerException);

        AssertEquivalentExceptRevision(checkpoint, manager.CaptureSnapshot());
    }

    [Fact]
    public async Task RegistrationDoesNotDependOnPageHeapCapacity()
    {
        var manager = new ClusterMeshes(16);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Oversized", Leaf()));
        Mesh handle = registration.Mesh;

        ClusterMeshesSnapshot registered = manager.CaptureSnapshot();
        Assert.Equal(1u, registration.PageCount);
        Assert.Equal(1u, registered.Pages.Registered);
        Assert.Equal(16u, registered.Heap.FreeBytes);
        Assert.False(manager.TryGetPublishedRoot(handle, out _));

        CompletePublication(manager);

        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(1, published.PublishedMeshCount);
        Assert.Equal(0u, published.Pages.Resident);
        Assert.True(manager.TryGetPublishedRoot(handle, out _));
        Assert.Equal(
            PageLoadResult.NoCapacity,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        Assert.Equal(16u, manager.CaptureSnapshot().Heap.FreeBytes);
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task ReportedPageUsageProtectsAHotResidentPageFromEviction()
    {
        var residency = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            GpuBytes = PageAllocationBytes * 2,
        });
        using var manager = new ClusterMeshes(
            PageAllocationBytes * 3,
            residency);
        ClusterMeshRegistration firstRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("First", Leaf()));
        ClusterMeshRegistration secondRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Second", Leaf()));
        ClusterMeshRegistration incomingRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Incoming", Leaf()));
        Mesh first = firstRegistration.Mesh;
        Mesh second = secondRegistration.Mesh;
        CompletePublication(manager);

        uint firstPageId = firstRegistration.FirstPageId;
        uint secondPageId = secondRegistration.FirstPageId;
        uint incomingPageId = incomingRegistration.FirstPageId;
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, firstPageId));
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, secondPageId));
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(first, out uint firstLeaf));
        Assert.True(manager.TryGetPublishedRoot(second, out uint secondLeaf));

        manager.ReportLeafUsage([firstLeaf]);
        Assert.Equal(
            PageLoadResult.Deferred,
            await ClusterTestAssets.LoadPageAsync(manager, incomingPageId));

        ClusterMeshesSnapshot evictionPending = manager.CaptureSnapshot();
        Assert.Equal(1, evictionPending.Pages.UncompletedEvictions);
        Assert.Equal(PageAllocationBytes * 2, evictionPending.Residency.GpuUsedBytes);
        Assert.True(manager.PublishPending());

        Assert.Equal(
            PageFaultResolutionKind.Satisfied,
            manager.ResolvePageFault(firstLeaf).Kind);
        Assert.Equal(
            PageFaultResolutionKind.NeedsLoad,
            manager.ResolvePageFault(secondLeaf).Kind);
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, incomingPageId));
        Assert.True(manager.PublishPending());
        Assert.Equal(PageAllocationBytes * 2, manager.CaptureSnapshot().Residency.GpuUsedBytes);
    }

    [Fact]
    public async Task InvalidMeshesCannotLeavePartialStateAndAValidMeshCanRegister()
    {
        var manager = new ClusterMeshes(PageAllocationBytes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddAuthoredMeshAsync(
                MeshWithBvh("BadLeaf", Leaf(localPage: 1))).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddAuthoredMeshAsync(
                MeshWithBvh(
                    "BadClusterRange",
                    Leaf(clusterStart: 1, clusterCount: 1))).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddAuthoredMeshAsync(
                MeshWithBvh("Disconnected", Leaf(), Leaf())).AsTask());

        Mesh badVertexRange = MeshWithBvh("BadVertexRange", Leaf());
        GPUCluster cluster = ReadCluster(badVertexRange);
        cluster.VertexStart = 1;
        WriteCluster(badVertexRange, cluster);
        await AssertClusterPayloadRejectedAsync(badVertexRange);

        Mesh badTriangleRange = MeshWithBvh("BadTriangleRange", Leaf());
        cluster = ReadCluster(badTriangleRange);
        cluster.PackedCounts = 1u | (2u << 8);
        WriteCluster(badTriangleRange, cluster);
        await AssertClusterPayloadRejectedAsync(badTriangleRange);

        Mesh badMaterialOffset = MeshWithBvh("BadMaterialOffset", Leaf());
        cluster = ReadCluster(badMaterialOffset);
        cluster.MaterialTableOffset = PageBytes;
        WriteCluster(badMaterialOffset, cluster);
        await AssertClusterPayloadRejectedAsync(badMaterialOffset);

        Mesh badBounds = MeshWithBvh("BadBounds", Leaf());
        cluster = ReadCluster(badBounds);
        cluster.BoundMin = new Vector3(float.NaN);
        WriteCluster(badBounds, cluster);
        await AssertClusterPayloadRejectedAsync(badBounds);

        ClusterMeshesSnapshot rejected = manager.CaptureSnapshot();
        Assert.Equal(0u, rejected.Pages.Registered);
        Assert.Equal(0, rejected.PublishedMeshCount);
        Assert.Equal(PageAllocationBytes, rejected.Heap.FreeBytes);
        Assert.False(manager.PublishPending());

        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Valid", Leaf()));
        Mesh handle = registration.Mesh;

        Assert.Equal(1u, registration.PageCount);
        Assert.Equal(1u, manager.CaptureSnapshot().Pages.Registered);
        Assert.False(manager.TryGetPublishedRoot(handle, out _));
        Assert.True(manager.PublishPending());
        Assert.True(manager.TryGetPublishedRoot(handle, out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue - 1)]
    public async Task UndefinedSlowMaterialTableOffsetsFailClosed(uint materialTableOffset)
    {
        Mesh asset = MeshWithBvh("SlowMaterial", Leaf());
        GPUCluster cluster = ReadCluster(asset);
        cluster.MaterialTableOffset = materialTableOffset;
        WriteCluster(asset, cluster);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Theory]
    [InlineData(0x01000000u, 0u)]
    [InlineData(0u, 0x00010000u)]
    [InlineData(0u, 0x00000001u)]
    [InlineData(0u, 0x00000200u)]
    public async Task NonCanonicalFastMaterialEncodingsFailClosed(
        uint packedMaterials,
        uint packedRanges)
    {
        Mesh asset = MeshWithBvh("FastMaterial", Leaf());
        GPUCluster cluster = ReadCluster(asset);
        cluster.PackedMaterials = packedMaterials;
        cluster.PackedRanges = packedRanges;
        WriteCluster(asset, cluster);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Theory]
    [InlineData(0x10000000u)]
    [InlineData(0x0A000000u)]
    [InlineData(0x00000001u)]
    [InlineData(0x00000020u)]
    public async Task NonCanonicalVrbEncodingsFailClosed(uint vrbBatchInfo)
    {
        Mesh asset = MeshWithBvh("VRB", Leaf());
        GPUCluster cluster = ReadCluster(asset);
        cluster.VRBBatchInfo = vrbBatchInfo;
        WriteCluster(asset, cluster);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Fact]
    public async Task QuantizedVertexIntegerAdditionCannotOverflow()
    {
        Mesh asset = MeshWithBvh("IntegerOverflow", Leaf());
        MeshPageHeader header = ReadPageHeader(asset);
        GPUCluster cluster = ReadCluster(asset);
        cluster.IntBaseX = int.MaxValue;
        WriteCluster(asset, cluster);
        BinaryPrimitives.WriteUInt16LittleEndian(
            asset.Payload!.Value.Span.Slice(checked((int)header.PositionsOffset), sizeof(ushort)),
            1);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Fact]
    public async Task FiniteQuantizationInputsCannotDecodeANonFiniteCoordinate()
    {
        Mesh asset = MeshWithBvh("CoordinateInfinity", Leaf());
        MeshPageHeader header = ReadPageHeader(asset);
        header.QuantStep = float.MaxValue;
        WritePageHeader(asset, header);
        GPUCluster cluster = ReadCluster(asset);
        cluster.IntBaseX = 2;
        WriteCluster(asset, cluster);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Fact]
    public async Task FiniteQuantizationInputsCannotDecodeANonFiniteRadius()
    {
        Mesh asset = MeshWithBvh("RadiusInfinity", Leaf());
        MeshPageHeader header = ReadPageHeader(asset);
        header.QuantStep = float.MaxValue;
        WritePageHeader(asset, header);
        GPUCluster cluster = ReadCluster(asset);
        cluster.PackedCenterZRadius = 2u << 16;
        WriteCluster(asset, cluster);

        await AssertClusterPayloadRejectedAsync(asset);
    }

    [Fact]
    public async Task BvhLeafRangesCannotOverlap()
    {
        Mesh asset = MeshWithPagesAndBvh(
            "OverlappingLeaves",
            [PageData()],
            Leaf(),
            Leaf(),
            Internal(firstChild: 0, childCount: 2));

        InvalidOperationException error = await AssertBvhRejectedAsync(asset);

        Assert.Contains("overlaps", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BvhLeafRangesCannotLeaveClusterGaps()
    {
        Mesh asset = MeshWithPagesAndBvh(
            "GappedLeaves",
            [PageData(clusterCount: 2)],
            Leaf(clusterStart: 1));

        InvalidOperationException error = await AssertBvhRejectedAsync(asset);

        Assert.Contains("leaves a gap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryClusterPageMustBeReferencedByTheBvh()
    {
        Mesh asset = MeshWithPagesAndBvh(
            "OrphanPage",
            [PageData(), PageData()],
            Leaf(localPage: 0));

        InvalidOperationException error = await AssertBvhRejectedAsync(asset);

        Assert.Contains("not referenced", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BvhNodeVectorsAndLodErrorMustBeFinite(int corruption)
    {
        ClusterBVHNode node = Leaf();
        switch (corruption)
        {
            case 0:
                node.BoundMin.X = float.NaN;
                break;
            case 1:
                node.BoundMax.Y = float.PositiveInfinity;
                break;
            case 2:
                node.LODSphere.Z = float.NegativeInfinity;
                break;
            case 3:
                node.LODError = float.NaN;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        InvalidOperationException error = await AssertBvhRejectedAsync(MeshWithBvh("NonFiniteBvh", node));

        Assert.Contains("invalid bounds or LOD data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyPendingRegistrationsPublishWholeMeshes()
    {
        const int meshCount = 32;
        using var manager = new ClusterMeshes(48);
        var meshes = new Mesh[meshCount];
        for (int index = 0; index < meshes.Length; index++)
            meshes[index] = MeshWithBvh($"Mesh{index}", Leaf());
        var registrations = new ClusterMeshRegistration[meshCount];
        for (int index = 0; index < meshCount; index++)
            registrations[index] = await manager.AddAuthoredMeshAsync(meshes[index]);

        ClusterMeshesSnapshot registered = manager.CaptureSnapshot();
        Assert.Equal((uint)meshCount, registered.Pages.Registered);
        Assert.Equal(0, registered.PublishedMeshCount);
        Assert.Equal(48u, registered.Heap.FreeBytes);
        var registeredPages = new HashSet<uint>();
        for (int index = 0; index < meshCount; index++)
        {
            Assert.Equal(1u, registrations[index].PageCount);
            Assert.True(registeredPages.Add(registrations[index].FirstPageId));
            Assert.False(manager.TryGetPublishedRoot(registrations[index].Mesh, out _));
        }
        Assert.Equal(meshCount, registeredPages.Count);

        Assert.True(manager.PublishPending());

        Assert.Equal(meshCount, manager.CaptureSnapshot().PublishedMeshCount);
        var publishedRoots = new HashSet<uint>();
        for (int index = 0; index < meshCount; index++)
        {
            Assert.True(manager.TryGetPublishedRoot(registrations[index].Mesh, out uint root));
            Assert.True(publishedRoots.Add(root));
        }
        Assert.Equal(meshCount, publishedRoots.Count);
        Assert.False(manager.PublishPending());
    }

    [Fact]
    public async Task DeepBvhUsesAnExplicitValidationStack()
    {
        const int nodeCount = 20_000;
        var nodes = new ClusterBVHNode[nodeCount];
        nodes[0] = Leaf();
        for (int node = 1; node < nodes.Length; node++)
        {
            nodes[node] = new ClusterBVHNode
            {
                ChildPointer = checked((uint)(node - 1)),
                ChildCount = 1,
                NodeType = 0,
            };
        }

        var manager = new ClusterMeshes(48);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Deep", nodes));
        Mesh handle = registration.Mesh;

        Assert.False(manager.TryGetPublishedRoot(handle, out _));
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(handle, out uint root));
        Assert.Equal(checked((uint)(nodeCount - 1)), root);
        Assert.Equal(48u, manager.CaptureSnapshot().Heap.FreeBytes);
    }

    [Fact]
    public async Task BvhPendingSetMustFitTheShaderTraversalStack()
    {
        int leafCount = ClusterBvh.TraversalStackCapacity + 1;
        var nodes = new ClusterBVHNode[leafCount + 1];
        for (int leaf = 0; leaf < leafCount; leaf++)
            nodes[leaf] = Leaf(clusterStart: checked((uint)leaf));
        nodes[^1] = Internal(0, checked((uint)leafCount));

        InvalidOperationException error = await AssertBvhRejectedAsync(
            MeshWithPagesAndBvh(
                "TraversalStackOverflow",
                [PageData(checked((uint)leafCount))],
                nodes));

        Assert.Contains("pending traversal entries", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            ClusterBvh.TraversalStackCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    private static void CompletePublication(ClusterMeshes manager)
        => Assert.True(manager.PublishPending());

    private static uint ReadChildPointer(ReadOnlySpan<byte> globalBvh, uint nodeIndex)
    {
        int nodeOffset = checked((int)(nodeIndex * ClusterBvh.NodeBytes));
        return MemoryMarshal.Read<ClusterBVHNode>(
            globalBvh.Slice(nodeOffset, checked((int)ClusterBvh.NodeBytes))).ChildPointer;
    }

    private static GPUCluster ReadCluster(Mesh asset)
        => MemoryMarshal.Read<GPUCluster>(
            asset.Payload!.Value.Span.Slice(MeshPageHeader.Size, GPUCluster.SizeInBytes));

    private static MeshPageHeader ReadPageHeader(Mesh asset)
        => MemoryMarshal.Read<MeshPageHeader>(
            asset.Payload!.Value.Span.Slice(0, MeshPageHeader.Size));

    private static void WriteCluster(Mesh asset, in GPUCluster cluster)
        => MemoryMarshal.Write(
            asset.Payload!.Value.Span.Slice(MeshPageHeader.Size, GPUCluster.SizeInBytes),
            in cluster);

    private static void WritePageHeader(Mesh asset, in MeshPageHeader header)
        => MemoryMarshal.Write(
            asset.Payload!.Value.Span.Slice(0, MeshPageHeader.Size),
            in header);

    private static Mesh MeshWithBvh(string name, params ClusterBVHNode[] nodes)
        => MeshWithPagesAndBvh(name, [PageData()], nodes);

    private static Mesh MeshWithPagesAndBvh(
        string name,
        ReadOnlyMemory<byte>[] pages,
        params ClusterBVHNode[] nodes)
    {
        int pageBytes = pages.Sum(static page => page.Length);
        byte[] payload = new byte[pageBytes + nodes.Length * Marshal.SizeOf<ClusterBVHNode>()];
        int pageOffset = 0;
        foreach (ReadOnlyMemory<byte> page in pages)
        {
            page.Span.CopyTo(payload.AsSpan(pageOffset));
            pageOffset = checked(pageOffset + page.Length);
        }
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(pageBytes));
        return new Mesh
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Payload = payload,
            VertexStride = VertexStride,
            BvhOffset = checked((ulong)pageBytes),
            QuantStep = 1f,
        };
    }

    private static ClusterBVHNode Leaf(
        uint localPage = 0,
        uint clusterStart = 0,
        uint clusterCount = 1)
    {
        var leaf = new ClusterBVHNode
        {
            ChildPointer = localPage,
            NodeType = 1,
        };
        leaf.SetLeafData(clusterStart, clusterCount);
        return leaf;
    }

    private static ClusterBVHNode Internal(uint firstChild, uint childCount)
        => new()
        {
            ChildPointer = firstChild,
            ChildCount = childCount,
            NodeType = 0,
        };

    private static byte[] PageData(uint clusterCount = 1)
    {
        int clusterBytes = checked((int)clusterCount * GPUCluster.SizeInBytes);
        int positionBytes = checked((int)clusterCount * PositionBytes);
        int vertexBytes = checked((int)clusterCount * (int)VertexStride);
        int indexBytes = checked((int)clusterCount * IndexBytes);
        int pageBytes = checked(MeshPageHeader.Size + clusterBytes + positionBytes
            + vertexBytes + indexBytes);
        byte[] data = new byte[pageBytes];
        var header = new MeshPageHeader
        {
            ClusterCount = clusterCount,
            TotalVertexCount = clusterCount,
            TotalTriangleCount = clusterCount,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = checked((uint)(MeshPageHeader.Size + clusterBytes)),
            AttributesOffset = checked((uint)(MeshPageHeader.Size + clusterBytes + positionBytes)),
            IndicesOffset = checked((uint)(MeshPageHeader.Size + clusterBytes
                + positionBytes + vertexBytes)),
            VertexStride = VertexStride,
            QuantStep = 1f,
        };
        MemoryMarshal.Write(data.AsSpan(), in header);
        for (uint index = 0; index < clusterCount; index++)
        {
            var cluster = new GPUCluster
            {
                LODRadius = 1f,
                PackedCenterZRadius = 1u << 16,
                VertexStart = checked((ushort)index),
                TriangleStart = checked((ushort)(index * IndexBytes)),
                PackedCounts = 1u | (1u << 8),
                MaterialTableOffset = uint.MaxValue,
                BoundMax = Vector3.One,
            };
            int clusterOffset = checked(MeshPageHeader.Size + ((int)index * GPUCluster.SizeInBytes));
            MemoryMarshal.Write(data.AsSpan(clusterOffset, GPUCluster.SizeInBytes), in cluster);
        }
        return data;
    }

    private static async Task AssertClusterPayloadRejectedAsync(Mesh asset)
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        int pageLength = checked((int)asset.BvhOffset);
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            asset,
            pageLength);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(
            controlled.Mesh);
        CompletePublication(manager);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId).AsTask());
        ClusterMeshesSnapshot rejected = manager.CaptureSnapshot();
        Assert.Equal(1u, rejected.Pages.Registered);
        Assert.Equal(0u, rejected.Pages.Resident);
        Assert.Equal(1u, rejected.Pages.Missing);
        Assert.Equal(PageAllocationBytes, rejected.Heap.FreeBytes);
        Assert.Equal(0, rejected.Residency.GpuUsedBytes);
        Assert.False(manager.PublishPending());
    }

    private static void AssertEquivalentExceptRevision(
        ClusterMeshesSnapshot expected,
        ClusterMeshesSnapshot actual)
    {
        Assert.True(actual.ManagerStateRevision > expected.ManagerStateRevision);
        Assert.Equal(
            expected with { ManagerStateRevision = actual.ManagerStateRevision },
            actual);
    }

    private static async Task<InvalidOperationException> AssertBvhRejectedAsync(Mesh asset)
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddAuthoredMeshAsync(asset).AsTask());
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Registered);
        Assert.False(manager.PublishPending());
        return error;
    }

    private sealed class FailOnceBvhStorage : IClusterBvhStorage
    {
        private readonly TestClusterBvhStorage _inner;
        private int _failNextRelease;

        internal FailOnceBvhStorage(Memory<byte> memory)
            => _inner = new TestClusterBvhStorage(memory);

        internal void FailNextRelease()
            => Volatile.Write(ref _failNextRelease, 1);

        public Memory<byte> Allocate(ulong offset, int length)
            => _inner.Allocate(offset, length);

        public Memory<byte> GetRange(ulong offset, int length)
            => _inner.GetRange(offset, length);

        public void Stage(ulong offset, int length)
            => _inner.Stage(offset, length);

        public void Publish()
            => _inner.Publish();

        public void Release(ulong offset, int length)
        {
            if (Interlocked.Exchange(ref _failNextRelease, 0) != 0)
                throw new InvalidOperationException("Injected BVH release failure.");
            _inner.Release(offset, length);
        }

        public void Dispose()
            => _inner.Dispose();
    }
}
