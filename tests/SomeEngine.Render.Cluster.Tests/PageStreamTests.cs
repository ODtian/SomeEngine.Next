using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class PageStreamTests
{
    private const int PositionBytes = 3 * sizeof(ushort);
    private const uint VertexStride = 16;
    private const int IndexBytes = 3;
    private const int PageBytes = MeshPageHeader.Size + GPUCluster.SizeInBytes
        + PositionBytes + (int)VertexStride + IndexBytes;
    private const uint PageAllocationBytes = (uint)((PageBytes + 15) & ~15);

    [Fact]
    public async Task HoldsDuplicatePageFaultsDuringLoad()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        var pageLoad = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.BeforeRead = async (_, _, _) =>
            await pageLoad.Task.ConfigureAwait(false);
        await using var stream = new PageStream(manager);

        uint[] faultWords = [2, fixture.FaultNode, fixture.FaultNode];
        var pageFaults = new PageFaults(manager.EpochId, capacity: 4096);
        PageFaultRead repeatedFaults = pageFaults.Read(
            MemoryMarshal.AsBytes(faultWords.AsSpan()));

        stream.Push(repeatedFaults);
        stream.Update();

        PageStreamSnapshot firstUpdate = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1, firstUpdate.Work.InFlightPages);
        Assert.Equal(0, firstUpdate.Work.QueuedPages);
        Assert.Equal(1u, firstUpdate.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1u, firstUpdate.LastUpdate.KnownLeafNodeIndices);
        Assert.Equal(0u, firstUpdate.LastUpdate.StagedPages);
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Resident);

        ReadOnlyMemory<uint> samePageFault = new[] { fixture.FaultNode };
        stream.Push(new PageFaultRead(manager.EpochId, 1, samePageFault.Span));
        stream.Update();

        PageStreamSnapshot repeatedUpdate = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1, repeatedUpdate.Work.InFlightPages);
        Assert.Equal(0, repeatedUpdate.Work.QueuedPages);
        Assert.Equal(1u, repeatedUpdate.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1u, repeatedUpdate.LastUpdate.KnownLeafNodeIndices);
        Assert.Equal(0u, repeatedUpdate.LastUpdate.StagedPages);
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Resident);

        pageLoad.SetResult(true);
        await UpdateUntilAsync(stream, () => stream.CaptureSnapshot().Work.InFlightPages == 0);
    }

    [Fact]
    public async Task SourceFailurePermanentlyRejectsPageWithoutRetry()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        fixture.Source.BeforeRead = static (_, _, _) =>
            ValueTask.FromException(new InvalidOperationException("page load failure"));
        await using var stream = new PageStream(manager);

        ReadOnlyMemory<uint> fault = new[] { fixture.FaultNode };
        stream.Push(new PageFaultRead(manager.EpochId, 1, fault.Span));
        await UpdateUntilAsync(
            stream,
            () => fixture.Source.TargetReadCount == 1 &&
                  stream.CaptureSnapshot().Work.InFlightPages == 0);

        PageStreamSnapshot failedLoad = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(0, failedLoad.Work.InFlightPages);
        Assert.Equal(0, failedLoad.Work.QueuedPages);
        Assert.Equal(0u, failedLoad.LastUpdate.StagedPages);
        Assert.Equal(1ul, failedLoad.Totals.LoadFailures);
        Assert.Equal(1, failedLoad.Work.PermanentlyFailedPages);
        PageStreamFailure failure = Assert.IsType<PageStreamFailure>(failedLoad.LastFailure);
        Assert.Equal(fixture.PageId, failure.PageId);
        Assert.Equal(PageStreamFailureCode.SourceReadFailed, failure.Code);
        Assert.Equal(2u, manager.CaptureSnapshot().Pages.Missing);

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault.Span));
        stream.Update();

        PageStreamSnapshot repeated = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1ul, repeated.Totals.LoadFailures);
        Assert.Equal(1, repeated.Work.PermanentlyFailedPages);
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Resident);
    }

    [Fact]
    public async Task InvalidDataFailureDuringAcquirePermanentlyRejectsPageWithoutRetry()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        fixture.Source.BeforeRead = static (_, _, _) =>
            ValueTask.FromException(new InvalidDataException("Cluster page authentication failed."));
        await using var stream = new PageStream(manager);
        uint[] fault = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        stream.Update();
        stream.Update();

        PageStreamSnapshot rejected = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1u, rejected.LastUpdate.FailedPages);
        Assert.Equal(1ul, rejected.Totals.LoadFailures);
        Assert.Equal(1, rejected.Work.PermanentlyFailedPages);
        Assert.Equal(0, rejected.Work.InFlightPages);
        Assert.Equal(0, rejected.Work.QueuedPages);
        PageStreamFailure failure = Assert.IsType<PageStreamFailure>(rejected.LastFailure);
        Assert.Equal(fixture.PageId, failure.PageId);
        Assert.Equal(PageStreamFailureCode.InvalidPayload, failure.Code);

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        stream.Update();

        PageStreamSnapshot repeatedFault = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1u, repeatedFault.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1u, repeatedFault.LastUpdate.KnownLeafNodeIndices);
        Assert.Equal(0u, repeatedFault.LastUpdate.FailedPages);
        Assert.Equal(1ul, repeatedFault.Totals.LoadFailures);
        Assert.Equal(1, repeatedFault.Work.PermanentlyFailedPages);
        Assert.Equal(0, repeatedFault.Work.InFlightPages);
        Assert.Equal(0, repeatedFault.Work.QueuedPages);
    }

    [Fact]
    public async Task PageWaitsForSafeHeapRetirementBeforeIo()
    {
        var manager = new ClusterMeshes(PageAllocationBytes * 2);
        ClusterMeshRegistration firstRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("First", Leaf()));
        using ControlledRuntimeMesh waiting = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            MeshWithBvh("Waiting", Leaf()),
            PageBytes);
        ClusterMeshRegistration waitingRegistration = await manager.AddMeshAsync(
            waiting.Mesh);
        Mesh waitingHandle = waitingRegistration.Mesh;
        PublishPending(manager);
        Assert.True(manager.TryGetPublishedRoot(waitingHandle, out uint waitingFaultNode));

        Assert.Equal(1u, firstRegistration.PageCount);
        Assert.Equal(1u, waitingRegistration.PageCount);
        uint firstPageID = firstRegistration.FirstPageId;
        uint waitingPageID = waitingRegistration.FirstPageId;
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, firstPageID));
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, waitingPageID));
        waiting.Source.Arm();
        PublishPending(manager);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.FreeBytes);

        Assert.True(manager.EvictPage(waitingPageID));
        PublishPending(manager);
        Assert.Equal(PageAllocationBytes, manager.CaptureSnapshot().Heap.FreeBytes);

        ClusterMeshRegistration replacementRegistration = await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Replacement", Leaf()));
        PublishPending(manager);
        Assert.Equal(1u, replacementRegistration.PageCount);
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, replacementRegistration.FirstPageId));
        PublishPending(manager);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.FreeBytes);

        await using var stream = new PageStream(manager);

        ReadOnlyMemory<uint> fault = new[] { waitingFaultNode };
        stream.Push(new PageFaultRead(manager.EpochId, 1, fault.Span));
        stream.Update();
        stream.Update();

        PageStreamSnapshot deferred = stream.CaptureSnapshot();
        ClusterMeshesSnapshot evictionStaged = manager.CaptureSnapshot();
        Assert.Equal(0, waiting.Source.TargetReadCount);
        Assert.Equal(1, deferred.Work.InFlightPages);
        Assert.Equal(0, deferred.Work.QueuedPages);
        Assert.Equal(0u, deferred.LastUpdate.StagedPages);
        Assert.Equal(0ul, deferred.Totals.LoadFailures);
        Assert.Equal(1, evictionStaged.Pages.UncompletedEvictions);
        Assert.Equal(0u, evictionStaged.Heap.FreeBytes);

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault.Span));
        stream.Update();
        stream.Update();
        Assert.Equal(0, waiting.Source.TargetReadCount);
        PageStreamSnapshot stillDeferred = stream.CaptureSnapshot();
        Assert.Equal(1, stillDeferred.Work.InFlightPages);
        Assert.Equal(0, stillDeferred.Work.QueuedPages);
        Assert.Equal(1, manager.CaptureSnapshot().Pages.UncompletedEvictions);

        PublishPending(manager);
        Assert.Equal(PageAllocationBytes, manager.CaptureSnapshot().Heap.FreeBytes);

        await UpdateUntilAsync(
            stream,
            () => waiting.Source.TargetReadCount == 1 &&
                  stream.CaptureSnapshot().Work.InFlightPages == 0);
        PageStreamSnapshot reloaded = stream.CaptureSnapshot();
        Assert.Equal(1, waiting.Source.TargetReadCount);
        Assert.Equal(0, reloaded.Work.QueuedPages);
        Assert.Equal(1u, reloaded.LastUpdate.StagedPages);
        Assert.Equal(1u, manager.CaptureSnapshot().Pages.Resident);

        PublishPending(manager);
        Assert.Equal(2u, manager.CaptureSnapshot().Pages.Resident);
    }

    [Fact]
    public async Task FaultOverflowIsDroppedAndCountedWithoutReplayState()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        await using var stream = new PageStream(manager);
        uint[] storedPages = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 5, storedPages));
        stream.Update();

        PageStreamSnapshot snapshot = stream.CaptureSnapshot();
        Assert.Equal(5ul, snapshot.LastUpdate.ReportedFaults);
        Assert.Equal(1ul, snapshot.LastUpdate.StoredFaults);
        Assert.Equal(4ul, snapshot.LastUpdate.DroppedFaults);
        Assert.Equal(4ul, snapshot.Totals.DroppedFaults);
    }

    [Fact]
    public async Task FaultInboxAdmitsOnlyConfiguredCapacityBeforeCoordinatorDrain()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        await using var stream = new PageStream(
            manager,
            maxPendingFaultWords: 2);
        uint[] firstBatch = [fixture.FaultNode, fixture.FaultNode];
        uint[] secondBatch = [fixture.FaultNode, fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 2, firstBatch));
        stream.Push(new PageFaultRead(manager.EpochId, 2, secondBatch));

        stream.Update();

        PageStreamSnapshot snapshot = stream.CaptureSnapshot();
        Assert.Equal(4ul, snapshot.LastUpdate.ReportedFaults);
        Assert.Equal(2ul, snapshot.LastUpdate.StoredFaults);
        Assert.Equal(2ul, snapshot.LastUpdate.DroppedFaults);
        Assert.Equal(2ul, snapshot.Totals.DroppedFaults);
        Assert.Equal(1u, snapshot.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1, fixture.Source.TargetReadCount);
    }

    [Fact]
    public async Task QueuedPageBackpressureIsBoundedIndependentlyFromFaultInbox()
    {
        const int pageCount = 5;
        const int maxQueuedPages = 2;
        using var manager = new ClusterMeshes();
        var meshes = new Mesh[pageCount];
        var sources = new ControlledRangeSource[pageCount];
        var loadCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        for (int index = 0; index < pageCount; index++)
        {
            using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
                MeshWithBvh($"Backpressure{index}", Leaf()),
                PageBytes);
            controlled.Source.BeforeRead = async (_, _, cancellationToken) =>
            {
                await loadCompletion.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            };
            sources[index] = controlled.Source;
            ClusterMeshRegistration registration = await manager.AddMeshAsync(
                controlled.Mesh);
            meshes[index] = registration.Mesh;
            Assert.Equal(1u, registration.PageCount);
        }
        PublishPending(manager);
        var faultNodes = new uint[pageCount];
        for (int index = 0; index < faultNodes.Length; index++)
            Assert.True(manager.TryGetPublishedRoot(meshes[index], out faultNodes[index]));

        await using var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxPendingFaultWords: pageCount,
            maxQueuedPages: maxQueuedPages);

        stream.Push(new PageFaultRead(manager.EpochId, pageCount, faultNodes));
        stream.Update();

        PageStreamSnapshot first = stream.CaptureSnapshot();
        Assert.Equal(1, sources.Sum(static source => source.TargetReadCount));
        Assert.Equal(1, first.Work.InFlightPages);
        Assert.InRange(first.Work.QueuedPages, 0, maxQueuedPages);
        Assert.Equal((ulong)pageCount, first.LastUpdate.StoredFaults);
        Assert.Equal(0ul, first.LastUpdate.DroppedFaults);
        Assert.Equal(3u, first.LastUpdate.BackpressuredPages);
        Assert.Equal(3ul, first.Totals.BackpressuredPages);

        stream.Push(new PageFaultRead(manager.EpochId, pageCount, faultNodes));
        stream.Update();

        PageStreamSnapshot second = stream.CaptureSnapshot();
        Assert.Equal(1, sources.Sum(static source => source.TargetReadCount));
        Assert.Equal(1, second.Work.InFlightPages);
        Assert.InRange(second.Work.QueuedPages, 0, maxQueuedPages);
        Assert.Equal((ulong)pageCount, second.LastUpdate.StoredFaults);
        Assert.Equal(0ul, second.LastUpdate.DroppedFaults);
        Assert.Equal(2u, second.LastUpdate.BackpressuredPages);
        Assert.Equal(5ul, second.Totals.BackpressuredPages);

        stream.Push(new PageFaultRead(manager.EpochId, pageCount, faultNodes));
        stream.Update();

        PageStreamSnapshot third = stream.CaptureSnapshot();
        Assert.Equal(1, sources.Sum(static source => source.TargetReadCount));
        Assert.Equal(1, third.Work.InFlightPages);
        Assert.InRange(third.Work.QueuedPages, 0, maxQueuedPages);
        Assert.Equal(2u, third.LastUpdate.BackpressuredPages);
        Assert.Equal(7ul, third.Totals.BackpressuredPages);

        stream.Dispose();
        loadCompletion.SetResult(true);
        await WaitUntilAsync(
            () => manager.CaptureSnapshot().ActivePageStreams == 0,
            "The backpressured page stream did not finish disposal.");
        Assert.Equal(0, manager.CaptureSnapshot().Residency.GpuUsedBytes);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.UsedBytes);
    }

    [Fact]
    public void DisposePreservesOverflowTelemetryAndEndsTheStream()
    {
        using var manager = new ClusterMeshes();
        using var stream = new PageStream(
            manager,
            maxPendingFaultWords: 1);
        uint[] overflowingBatch = [0, 0];

        stream.Push(new PageFaultRead(manager.EpochId, 2, overflowingBatch));

        stream.Dispose();

        PageStreamSnapshot terminal = stream.CaptureSnapshot();
        Assert.Equal(PageStreamLifecycle.Disposed, terminal.Lifecycle);
        Assert.Equal(1ul, terminal.Totals.DroppedFaults);
        Assert.Equal(0, manager.CaptureSnapshot().ActivePageStreams);
    }

    [Fact]
    public async Task RepeatedOverflowAccumulatesDropTelemetry()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        await using var stream = new PageStream(
            manager,
            maxPendingFaultWords: 1);
        uint[] overflowingBatch = [fixture.FaultNode, fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 2, overflowingBatch));
        stream.Update();
        Assert.Equal(1ul, stream.CaptureSnapshot().Totals.DroppedFaults);

        stream.Push(new PageFaultRead(manager.EpochId, 2, overflowingBatch));
        stream.Update();
        Assert.Equal(2ul, stream.CaptureSnapshot().Totals.DroppedFaults);
    }

    [Fact]
    public void FaultTelemetryAccumulatesBeyondUintWithoutOverflow()
    {
        using var manager = new ClusterMeshes();
        using var stream = new PageStream(manager);

        stream.Push(new PageFaultRead(manager.EpochId, uint.MaxValue, ReadOnlySpan<uint>.Empty));
        stream.Push(new PageFaultRead(manager.EpochId, uint.MaxValue, ReadOnlySpan<uint>.Empty));
        stream.Update();

        const ulong expected = 2ul * uint.MaxValue;
        PageStreamSnapshot snapshot = stream.CaptureSnapshot();
        Assert.Equal(expected, snapshot.LastUpdate.ReportedFaults);
        Assert.Equal(0ul, snapshot.LastUpdate.StoredFaults);
        Assert.Equal(expected, snapshot.LastUpdate.DroppedFaults);
        Assert.Equal(expected, snapshot.Totals.DroppedFaults);
    }

    [Fact]
    public void CrossEpochFaultsAreRejectedWithoutChangingStreamerState()
    {
        using var manager = new ClusterMeshes();
        using var foreignManager = new ClusterMeshes();
        using var stream = new PageStream(manager);
        uint[] pages = [0];

        Assert.Throws<ArgumentException>(() =>
            stream.Push(new PageFaultRead(foreignManager.EpochId, 1, pages)));

        stream.Update();

        PageStreamSnapshot snapshot = stream.CaptureSnapshot();
        Assert.Equal(0ul, snapshot.LastUpdate.ReportedFaults);
        Assert.Equal(0ul, snapshot.LastUpdate.StoredFaults);
        Assert.Equal(0ul, snapshot.LastUpdate.DroppedFaults);
        Assert.Equal(0ul, snapshot.Totals.DroppedFaults);
        Assert.Equal(0, snapshot.Work.QueuedPages);
        Assert.Equal(0, snapshot.Work.InFlightPages);
    }

    [Fact]
    public async Task LateCompletionAfterDisposeReleasesFinalHeapReservation()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        };
        var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);
        uint[] pages = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 1, pages));
        stream.Update();
        ClusterMeshesSnapshot loading = manager.CaptureSnapshot();
        Assert.Equal(PageAllocationBytes, loading.Residency.GpuUsedBytes);
        Assert.Equal(PageAllocationBytes, loading.Heap.UsedBytes);

        stream.Dispose();
        completion.SetResult(true);

        await WaitUntilAsync(
            () => manager.CaptureSnapshot().ActivePageStreams == 0 &&
                manager.CaptureSnapshot().Residency.GpuUsedBytes == 0,
            "The late page completion did not release its final PageHeap reservation.");

        Assert.Equal(0, manager.CaptureSnapshot().Residency.GpuUsedBytes);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.UsedBytes);
        Assert.Throws<ObjectDisposedException>(stream.Update);
    }

    [Fact]
    public async Task InFlightPageStreamPreventsManagerDisposalUntilStreamTerminationCompletes()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        ClusterMeshes manager = fixture.Manager;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        };
        var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);

        uint[] pages = [fixture.FaultNode];
        stream.Push(new PageFaultRead(manager.EpochId, 1, pages));
        stream.Update();
        ClusterMeshesSnapshot active = manager.CaptureSnapshot();
        Assert.Equal(1, stream.CaptureSnapshot().Work.InFlightPages);
        Assert.Equal(1, active.ActivePageStreams);
        Assert.Equal(PageAllocationBytes, active.Residency.GpuUsedBytes);
        Assert.Equal(PageAllocationBytes, active.Heap.UsedBytes);

        Assert.Throws<InvalidOperationException>(manager.Dispose);
        Assert.Equal(active, manager.CaptureSnapshot());

        stream.Dispose();
        Assert.Throws<InvalidOperationException>(manager.Dispose);

        completion.SetResult(true);
        await WaitUntilAsync(
            () => manager.CaptureSnapshot().ActivePageStreams == 0,
            "The terminating page stream did not release its manager lifecycle lease.");

        Assert.Equal(0, manager.CaptureSnapshot().Residency.GpuUsedBytes);
        Assert.Equal(0u, manager.CaptureSnapshot().Heap.UsedBytes);
        manager.Dispose();
        ClusterMeshesSnapshot terminal = manager.CaptureSnapshot();
        Assert.Equal(ClusterLifecycle.Disposed, terminal.Lifecycle);
        Assert.Equal(0, terminal.ActivePageStreams);
        Assert.Equal(0, terminal.Residency.GpuUsedBytes);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotStrandStreamDisposal()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        ClusterMeshes manager = fixture.Manager;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellationRegistration = default;
        fixture.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            cancellationRegistration = cancellationToken.Register(
                static () => throw new InvalidOperationException("Faulty loader cancellation callback."));
            await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        };
        var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);
        uint[] fault = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        stream.Update();
        Assert.Equal(1, manager.CaptureSnapshot().ActivePageStreams);
        Assert.Equal(PageAllocationBytes, manager.CaptureSnapshot().Residency.GpuUsedBytes);
        Assert.Equal(PageAllocationBytes, manager.CaptureSnapshot().Heap.UsedBytes);

        Exception? disposeFailure = Record.Exception(stream.Dispose);

        Assert.Null(disposeFailure);
        Assert.Equal(PageStreamLifecycle.Disposing, stream.CaptureSnapshot().Lifecycle);
        Assert.Equal(1, manager.CaptureSnapshot().ActivePageStreams);

        completion.SetResult(true);
        await WaitUntilAsync(
            () => stream.CaptureSnapshot().Lifecycle == PageStreamLifecycle.Disposed &&
                manager.CaptureSnapshot().ActivePageStreams == 0,
            "The page stream did not complete termination after its loader finished.");

        PageStreamSnapshot terminalStream = stream.CaptureSnapshot();
        ClusterMeshesSnapshot terminalManagerLease = manager.CaptureSnapshot();
        Assert.Equal(PageStreamLifecycle.Disposed, terminalStream.Lifecycle);
        Assert.Equal(0, terminalManagerLease.ActivePageStreams);
        Assert.Equal(0, terminalManagerLease.Residency.GpuUsedBytes);
        Assert.Equal(0u, terminalManagerLease.Heap.UsedBytes);

        cancellationRegistration.Dispose();
        manager.Dispose();
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotBlockDisposeEntry()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        ClusterMeshes manager = fixture.Manager;
        var loadCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        CancellationTokenRegistration cancellationRegistration = default;
        fixture.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            cancellationRegistration = cancellationToken.Register(() =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            });
            await loadCompletion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        };
        var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);

        stream.Push(new PageFaultRead(
            manager.EpochId,
            1,
            new uint[] { fixture.FaultNode }));
        stream.Update();

        await Task.Run(stream.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        bool callbackWasBlocking = callbackEntered.Wait(TimeSpan.FromSeconds(5));
        Assert.Equal(PageStreamLifecycle.Disposing, stream.CaptureSnapshot().Lifecycle);

        releaseCallback.Set();
        loadCompletion.SetResult(true);
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(callbackWasBlocking);
        Assert.Equal(PageStreamLifecycle.Disposed, stream.CaptureSnapshot().Lifecycle);
        Assert.Equal(0, manager.CaptureSnapshot().ActivePageStreams);
        cancellationRegistration.Dispose();
        manager.Dispose();
    }

    [Fact]
    public async Task ConcurrentFaultProducersAreMergedByTheCoordinator()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        await using var stream = new PageStream(manager);

        Parallel.For(0, 64, _ =>
        {
            uint[] onePage = [fixture.FaultNode];
            stream.Push(new PageFaultRead(manager.EpochId, 1, onePage));
        });
        stream.Update();

        PageStreamSnapshot snapshot = stream.CaptureSnapshot();
        Assert.Equal(64ul, snapshot.LastUpdate.ReportedFaults);
        Assert.Equal(64ul, snapshot.LastUpdate.StoredFaults);
        Assert.Equal(0ul, snapshot.LastUpdate.DroppedFaults);
        Assert.Equal(1u, snapshot.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1, snapshot.Work.InFlightPages);
    }

    [Fact]
    public async Task LoadConcurrencyBudgetLeavesExcessFaultsQueued()
    {
        const int pageCount = 4;
        using var manager = new ClusterMeshes();
        var meshes = new Mesh[pageCount];
        var completions = new TaskCompletionSource<bool>[pageCount];
        var started = new List<uint>();
        for (int index = 0; index < pageCount; index++)
        {
            completions[index] = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
                MeshWithBvh($"Budget{index}", Leaf()),
                PageBytes);
            ClusterMeshRegistration registration = await manager.AddMeshAsync(
                controlled.Mesh);
            meshes[index] = registration.Mesh;
            uint pageId = registration.FirstPageId;
            controlled.Source.BeforeRead = async (_, _, cancellationToken) =>
            {
                started.Add(pageId);
                await completions[checked((int)pageId)].Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            };
            Assert.Equal(1u, registration.PageCount);
        }
        PublishPending(manager);
        var faultNodes = new uint[pageCount];
        for (int index = 0; index < faultNodes.Length; index++)
            Assert.True(manager.TryGetPublishedRoot(meshes[index], out faultNodes[index]));

        await using var stream = new PageStream(
            manager,
            maxInFlightLoads: 2);

        stream.Push(new PageFaultRead(manager.EpochId, pageCount, faultNodes));
        stream.Update();

        PageStreamSnapshot limited = stream.CaptureSnapshot();
        Assert.Equal(2, started.Count);
        Assert.Equal(2, limited.Work.InFlightPages);
        Assert.Equal(2, limited.Work.QueuedPages);

        int firstPairCount = started.Count;
        for (int index = 0; index < firstPairCount; index++)
            completions[checked((int)started[index])].SetResult(true);
        await UpdateUntilAsync(stream, () => started.Count == pageCount);

        PageStreamSnapshot secondPair = stream.CaptureSnapshot();
        Assert.Equal(4, started.Count);
        Assert.Equal(2, secondPair.Work.InFlightPages);
        Assert.Equal(0, secondPair.Work.QueuedPages);

        for (int index = firstPairCount; index < started.Count; index++)
            completions[checked((int)started[index])].SetResult(true);
        await UpdateUntilAsync(stream, () => stream.CaptureSnapshot().Work.InFlightPages == 0);
    }

    [Fact]
    public async Task FaultDuringPendingEvictionCancelsRetirementWithoutIo()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            MeshWithBvh("Retiring", Leaf()),
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh);
        Mesh mesh = registration.Mesh;
        PublishPending(manager);
        Assert.True(manager.TryGetPublishedRoot(mesh, out uint faultNode));
        Assert.Equal(1u, registration.PageCount);
        uint pageID = registration.FirstPageId;
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, pageID));
        controlled.Source.Arm();
        PublishPending(manager);

        Assert.True(manager.EvictPage(pageID));
        ClusterMeshesSnapshot pendingEviction = manager.CaptureSnapshot();
        Assert.Equal(1, pendingEviction.Pages.UncompletedEvictions);
        Assert.Equal(1u, pendingEviction.Pages.Resident);
        Assert.Equal(0u, pendingEviction.Heap.FreeBytes);

        await using var stream = new PageStream(manager);
        uint[] pageFaults = [faultNode];
        stream.Push(new PageFaultRead(manager.EpochId, 1, pageFaults));
        stream.Update();

        Assert.Equal(0, controlled.Source.TargetReadCount);
        PageStreamSnapshot satisfiedFault = stream.CaptureSnapshot();
        Assert.Equal(0, satisfiedFault.Work.InFlightPages);
        Assert.Equal(0, satisfiedFault.Work.QueuedPages);
        Assert.Equal(1u, satisfiedFault.LastUpdate.KnownLeafNodeIndices);
        Assert.Equal(0u, satisfiedFault.LastUpdate.StagedPages);

        ClusterMeshesSnapshot cancelledEviction = manager.CaptureSnapshot();
        Assert.Equal(0, cancelledEviction.Pages.UncompletedEvictions);
        Assert.Equal(1u, cancelledEviction.Pages.Resident);
        Assert.Equal(0u, cancelledEviction.Heap.FreeBytes);

        PublishPending(manager);
        Assert.Equal(
            PageFaultResolutionKind.Satisfied,
            manager.ResolvePageFault(faultNode).Kind);
        Assert.Equal(1u, manager.CaptureSnapshot().Pages.Resident);
    }

    [Fact]
    public async Task RangeSourceReceivesExactFinalPageMemory()
    {
        var storageBytes = new byte[PageAllocationBytes];
        MissingPageFixture fixture = await MissingPageAsync(
            new TestClusterPageStorage(storageBytes));
        using ClusterMeshes manager = fixture.Manager;
        int destinationLength = 0;
        ArraySegment<byte> finalDestination = default;
        fixture.Source.BeforeRead = (_, destination, _) =>
        {
            destinationLength = destination.Length;
            Assert.True(MemoryMarshal.TryGetArray(
                (ReadOnlyMemory<byte>)destination,
                out finalDestination));
            return ValueTask.CompletedTask;
        };
        await using var stream = new PageStream(manager);
        uint[] fault = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        await UpdateUntilAsync(
            stream,
            () => fixture.Source.TargetReadCount == 1 &&
                  stream.CaptureSnapshot().Work.InFlightPages == 0);

        PageStreamSnapshot stagedStream = stream.CaptureSnapshot();
        ClusterMeshesSnapshot staged = manager.CaptureSnapshot();
        Assert.Equal(PageBytes, destinationLength);
        Assert.Same(storageBytes, finalDestination.Array);
        Assert.Equal(1u, stagedStream.LastUpdate.StagedPages);
        Assert.Equal(0, stagedStream.Work.PermanentlyFailedPages);
        Assert.Equal(0, stagedStream.Work.InFlightPages);
        Assert.Equal(0, stagedStream.Work.QueuedPages);
        Assert.Equal(1, staged.Pages.UncompletedLoads);

        Assert.True(manager.PublishPending());

        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(1u, published.Pages.Resident);
        Assert.Equal(0, published.Pages.UncompletedLoads);
        Assert.Equal(
            PageFaultResolutionKind.Satisfied,
            manager.ResolvePageFault(fixture.FaultNode).Kind);
    }

    [Fact]
    public async Task InvalidLoadedPageBecomesPermanentFailureWithoutRepeatedIo()
    {
        MissingPageFixture fixture = await MissingPageAsync();
        using ClusterMeshes manager = fixture.Manager;
        fixture.Source.AfterRead = static (_, destination) =>
        {
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(destination.Span);
            header.QuantStep = float.NaN;
            MemoryMarshal.Write(destination.Span, in header);
        };
        await using var stream = new PageStream(manager);
        uint[] fault = [fixture.FaultNode];

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        await UpdateUntilAsync(
            stream,
            () => stream.CaptureSnapshot().Work.InFlightPages == 0 &&
                  fixture.Source.TargetReadCount == 1);

        PageStreamSnapshot rejected = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1u, rejected.LastUpdate.FailedPages);
        Assert.Equal(1ul, rejected.Totals.LoadFailures);
        Assert.Equal(1, rejected.Work.PermanentlyFailedPages);
        Assert.Equal(0, rejected.Work.InFlightPages);
        Assert.Equal(0, rejected.Work.QueuedPages);
        PageStreamFailure failure = Assert.IsType<PageStreamFailure>(rejected.LastFailure);
        Assert.Equal(fixture.PageId, failure.PageId);
        Assert.Equal(PageStreamFailureCode.InvalidPayload, failure.Code);
        Assert.Equal(0u, manager.CaptureSnapshot().Pages.Resident);

        stream.Push(new PageFaultRead(manager.EpochId, 1, fault));
        stream.Update();

        PageStreamSnapshot repeatedFault = stream.CaptureSnapshot();
        Assert.Equal(1, fixture.Source.TargetReadCount);
        Assert.Equal(1u, repeatedFault.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1u, repeatedFault.LastUpdate.KnownLeafNodeIndices);
        Assert.Equal(0u, repeatedFault.LastUpdate.FailedPages);
        Assert.Equal(1ul, repeatedFault.Totals.LoadFailures);
        Assert.Equal(1, repeatedFault.Work.PermanentlyFailedPages);
        Assert.Equal(0, repeatedFault.Work.InFlightPages);
        Assert.Equal(0, repeatedFault.Work.QueuedPages);
    }

    private static async ValueTask<MissingPageFixture> MissingPageAsync(
        IClusterPageStorage? pageStorage = null)
    {
        var manager = pageStorage is null
            ? new ClusterMeshes()
            : new ClusterMeshes(PageHeap.CapacityBytes, residency: null, pageStorage);
        await manager.AddAuthoredMeshAsync(
            MeshWithBvh("Seed", Leaf(), Internal(firstChild: 0, childCount: 1)));
        PublishPending(manager);

        Mesh asset = MeshWithBvh("PageStream", Leaf());
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            asset,
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh);
        Mesh mesh = registration.Mesh;
        Assert.Same(controlled.Mesh, mesh);
        Assert.Equal(1u, registration.PageCount);
        uint pageID = registration.FirstPageId;
        Assert.False(manager.TryGetPublishedRoot(mesh, out _));
        ClusterMeshesSnapshot pendingRegistration = manager.CaptureSnapshot();
        Assert.Equal(0u, pendingRegistration.Pages.Resident);
        Assert.Equal(1, pendingRegistration.PublishedMeshCount);

        PublishPending(manager);
        Assert.True(manager.TryGetPublishedRoot(mesh, out uint globalNodeIndex));
        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(0u, published.Pages.Resident);
        Assert.Equal(2, published.PublishedMeshCount);
        PageFaultResolution resolution = manager.ResolvePageFault(globalNodeIndex);
        Assert.Equal(PageFaultResolutionKind.NeedsLoad, resolution.Kind);
        Assert.Equal(pageID, resolution.PageId);

        ClusterMeshesSnapshot ready = manager.CaptureSnapshot();
        Assert.Equal(0u, ready.Pages.Resident);
        Assert.Equal(2u, ready.Pages.Missing);
        Assert.Equal(0u, ready.Pages.TotalCompletedEvictions);
        Assert.Equal(0, ready.Pages.UncompletedEvictions);
        return new MissingPageFixture(
            manager,
            pageID,
            globalNodeIndex,
            controlled.Source);
    }

    private readonly record struct MissingPageFixture(
        ClusterMeshes Manager,
        uint PageId,
        uint FaultNode,
        ControlledRangeSource Source);

    private static void PublishPending(ClusterMeshes manager)
    {
        Assert.True(manager.PublishPending());
    }

    private static async Task UpdateUntilAsync(PageStream stream, Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            stream.Update();
            if (condition())
                return;
            await Task.Delay(1);
        }

        Assert.Fail("The page streamer did not reach the expected state.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail(failureMessage);
    }

    private static Mesh MeshWithBvh(string name, params ClusterBVHNode[] nodes)
    {
        byte[] payload = new byte[PageBytes + nodes.Length * Marshal.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes,
            AttributesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes,
            IndicesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes
                + PositionBytes + VertexStride,
            VertexStride = VertexStride,
            QuantStep = 1f,
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        var cluster = new GPUCluster
        {
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
        };
        MemoryMarshal.Write(payload.AsSpan(MeshPageHeader.Size, GPUCluster.SizeInBytes), in cluster);
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(PageBytes));
        return new Mesh
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Payload = payload,
            VertexStride = VertexStride,
            BvhOffset = PageBytes,
            QuantStep = 1f,
        };
    }

    private static ClusterBVHNode Leaf()
    {
        var leaf = new ClusterBVHNode
        {
            ChildPointer = 0,
            NodeType = 1,
        };
        leaf.SetLeafData(0, 1);
        return leaf;
    }

    private static ClusterBVHNode Internal(uint firstChild, uint childCount)
        => new()
        {
            ChildPointer = firstChild,
            ChildCount = childCount,
            NodeType = 0,
        };

    private static byte[] PageData()
    {
        byte[] data = new byte[PageBytes];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes,
            AttributesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes,
            IndicesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes
                + PositionBytes + VertexStride,
            VertexStride = VertexStride,
            QuantStep = 1f,
        };
        MemoryMarshal.Write(data.AsSpan(), in header);
        var cluster = new GPUCluster
        {
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
        };
        MemoryMarshal.Write(data.AsSpan(MeshPageHeader.Size, GPUCluster.SizeInBytes), in cluster);
        return data;
    }

}
