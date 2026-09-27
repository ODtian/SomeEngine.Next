using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ResidencyBudgetTests
{
    private const int PageBytes = ClusterTestAssets.PageBytes;
    private const int GpuPageBytes = (PageBytes + 15) & ~15;

    [Fact]
    public async Task GpuReservationSpansStagingPublicationAndRetirement()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger);
        using ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;
        uint faultNode = fixture.FirstFaultNode;
        Assert.Equal(1u, registration.PageCount);
        uint pageId = registration.FirstPageId;
        await using var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);

        stream.Push(new PageFaultRead(manager.EpochId, 1, new[] { faultNode }));
        stream.Update();

        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        await UpdateUntilAsync(
            stream,
            () => stream.CaptureSnapshot().LastUpdate.StagedPages == 1);

        PageStreamSnapshot stagedSnapshot = stream.CaptureSnapshot();
        Assert.Equal(1u, stagedSnapshot.LastUpdate.StagedPages);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        CompletePublication(manager);

        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        Assert.Equal(1u, manager.CaptureSnapshot().Pages.Resident);

        Assert.True(manager.EvictPage(pageId));
        CompletePublication(manager);

        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        Assert.Equal(0, ledger.Used(ResidencyClass.Compressed));
        AssertWithinBudgets(ledger);
    }

    [Fact]
    public async Task RangeSourceTargetsFinalStorageWithoutAPublicationCopy()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        var storageBytes = new byte[GpuPageBytes];
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger,
            pageStorage: new TestClusterPageStorage(storageBytes));
        using ClusterMeshes manager = fixture.Manager;
        ArraySegment<byte> finalDestination = default;
        fixture.Source.BeforeRead = (_, destination, _) =>
        {
            Assert.True(MemoryMarshal.TryGetArray(
                (ReadOnlyMemory<byte>)destination,
                out finalDestination));
            return ValueTask.CompletedTask;
        };
        await using var stream = new PageStream(manager);

        stream.Push(new PageFaultRead(manager.EpochId, 1, [fixture.FirstFaultNode]));
        await UpdateUntilAsync(
            stream,
            () => fixture.Source.TargetReadCount == 1 &&
                  stream.CaptureSnapshot().Work.InFlightPages == 0);

        Assert.Same(storageBytes, finalDestination.Array);
        Assert.Equal(PageBytes, finalDestination.Count);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        ClusterMeshesSnapshot pending = manager.CaptureSnapshot();
        Assert.Equal(1, pending.Pages.UncompletedLoads);
        Assert.Equal(0u, pending.Pages.Resident);
        Assert.Equal(PageData()[0], storageBytes[finalDestination.Offset]);

        Assert.True(manager.SubmitPending());
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        ClusterMeshesSnapshot published = manager.CaptureSnapshot();
        Assert.Equal(0, published.Pages.UncompletedLoads);
        Assert.Equal(1u, published.Pages.Resident);
    }

    [Fact]
    public async Task ConcurrentFaultsBackpressureAtEachResidencyBudgetWithoutOversubscription()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 2,
            heapBytes: 2 * GpuPageBytes,
            ledger);
        using ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;
        uint firstFaultNode = fixture.FirstFaultNode;
        Assert.Equal(2u, registration.PageCount);
        uint firstPageId = registration.FirstPageId;
        uint secondPageId = checked(firstPageId + 1);
        await using var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);

        stream.Push(new PageFaultRead(
            manager.EpochId,
            2,
            new[] { firstFaultNode, checked(firstFaultNode + 1) }));
        stream.Update();

        PageStreamSnapshot firstAdmissionSnapshot = stream.CaptureSnapshot();
        Assert.Equal(1, firstAdmissionSnapshot.Work.InFlightPages);
        Assert.Equal(1, firstAdmissionSnapshot.Work.QueuedPages);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        await UpdateUntilAsync(
            stream,
            () => stream.CaptureSnapshot().LastUpdate.StagedPages == 1);

        PageStreamSnapshot firstStagedSnapshot = stream.CaptureSnapshot();
        Assert.Equal(1u, firstStagedSnapshot.LastUpdate.StagedPages);
        Assert.Equal(
            1,
            firstStagedSnapshot.Work.QueuedPages +
            firstStagedSnapshot.Work.InFlightPages);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        CompletePublication(manager);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));

        await UpdateUntilAsync(
            stream,
            () => manager.CaptureSnapshot().Pages.UncompletedEvictions == 1);
        Assert.Equal(1, manager.CaptureSnapshot().Pages.UncompletedEvictions);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        CompletePublication(manager);
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));

        await UpdateUntilAsync(
            stream,
            () => manager.CaptureSnapshot().Pages.UncompletedLoads == 1);
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);

        CompletePublication(manager);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));

        Assert.True(manager.EvictPage(secondPageId));
        CompletePublication(manager);
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);
    }

    [Fact]
    public async Task InvalidPageFailureReleasesItsFinalGpuReservation()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger);
        using ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;
        uint faultNode = fixture.FirstFaultNode;
        Assert.Equal(1u, registration.PageCount);
        uint pageId = registration.FirstPageId;
        fixture.Source.AfterRead = static (_, destination) => destination.Span[^1] ^= 1;
        using var stream = new PageStream(manager);

        stream.Push(new PageFaultRead(manager.EpochId, 1, new[] { faultNode }));
        await UpdateUntilAsync(
            stream,
            () => stream.CaptureSnapshot().Work.InFlightPages == 0 &&
                  fixture.Source.TargetReadCount == 1);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));

        PageStreamSnapshot failureSnapshot = stream.CaptureSnapshot();
        Assert.Equal(1ul, failureSnapshot.Totals.LoadFailures);
        PageStreamFailure failure = Assert.IsType<PageStreamFailure>(failureSnapshot.LastFailure);
        Assert.Equal(pageId, failure.PageId);
        Assert.Equal(PageStreamFailureCode.InvalidPayload, failure.Code);
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        Assert.False(manager.SubmitPending());
        AssertWithinBudgets(ledger);
    }

    [Fact]
    public async Task FinalStorageCleanupFailurePreservesPrimaryErrorAndQuarantinesHeapRange()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        var storage = new ShortAllocationStorage();
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger,
            pageStorage: storage);
        using ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;

        InvalidOperationException primary = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));

        Assert.Contains("Cluster page staging storage returned", primary.Message, StringComparison.Ordinal);
        ClusterMeshesSnapshot snapshot = manager.CaptureSnapshot();
        ClusterCleanupFailure cleanup = Assert.IsType<ClusterCleanupFailure>(snapshot.LastCleanupFailure);
        Assert.Equal(ClusterCleanupStage.PageLoad, cleanup.Stage);
        Assert.Contains("Injected staging-storage release failure", cleanup.Message, StringComparison.Ordinal);
        Assert.Equal(checked((uint)GpuPageBytes), snapshot.Heap.UsedBytes);
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        Assert.False(manager.SubmitPending());
    }

    [Fact]
    public async Task CancellationReleasesInFlightFinalGpuReservation()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger);
        using ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;
        uint faultNode = fixture.FirstFaultNode;
        Assert.Equal(1u, registration.PageCount);
        uint pageId = registration.FirstPageId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using var stream = new PageStream(
            manager,
            maxInFlightLoads: 1,
            maxRetainedBytes: MeshPageHeader.MaxPageSize);

        stream.Push(new PageFaultRead(manager.EpochId, 1, new[] { faultNode }));
        stream.Update();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));

        stream.Dispose();
        for (int attempt = 0;
             attempt < 100 &&
             (ledger.Used(ResidencyClass.Gpu) != 0 ||
              manager.CaptureSnapshot().ActivePageStreams != 0);
             attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        Assert.Equal(0, manager.CaptureSnapshot().ActivePageStreams);
        Assert.Equal(PageStreamLifecycle.Disposed, stream.CaptureSnapshot().Lifecycle);
        AssertWithinBudgets(ledger);
    }

    [Fact]
    public async Task ManagerDisposalReleasesFinalGpuReservations()
    {
        ResidencyBudgetLedger ledger = Ledger(stagingBytes: 0, GpuPageBytes);
        RegisteredManagerFixture fixture = await RegisteredManagerAsync(
            pageCount: 1,
            heapBytes: GpuPageBytes,
            ledger);
        ClusterMeshes manager = fixture.Manager;
        ClusterMeshRegistration registration = fixture.Registration;
        Assert.Equal(1u, registration.PageCount);
        uint pageId = registration.FirstPageId;

        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, pageId));
        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(GpuPageBytes, ledger.Used(ResidencyClass.Gpu));

        manager.Dispose();

        Assert.Equal(0, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(0, ledger.Used(ResidencyClass.Gpu));
        AssertWithinBudgets(ledger);
    }

    private static async ValueTask<RegisteredManagerFixture> RegisteredManagerAsync(
        int pageCount,
        int heapBytes,
        ResidencyBudgetLedger ledger,
        IClusterPageStorage? pageStorage = null)
    {
        var manager = new ClusterMeshes(checked((uint)heapBytes), ledger, pageStorage);
        using ControlledRuntimeMesh controlled = await ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            Mesh(pageCount),
            PageBytes);
        ClusterMeshRegistration registration = await manager.AddMeshAsync(controlled.Mesh, pageSource: controlled.PageSource);
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(registration.Mesh, out uint root));
        uint firstFaultNode = pageCount == 1 ? root : checked(root - 2);
        return new RegisteredManagerFixture(
            manager,
            registration,
            firstFaultNode,
            controlled.Source);
    }

    private readonly record struct RegisteredManagerFixture(
        ClusterMeshes Manager,
        ClusterMeshRegistration Registration,
        uint FirstFaultNode,
        ControlledRangeSource Source);

    private static ResidencyBudgetLedger Ledger(long stagingBytes, long gpuBytes)
        => new(new ResidencyBudgets
        {
            CompressedBytes = 0,
            DecodedCpuBytes = 0,
            UploadStagingBytes = stagingBytes,
            GpuBytes = gpuBytes,
        });

    private static void AssertWithinBudgets(ResidencyBudgetLedger ledger)
    {
        foreach (ResidencyClass residencyClass in Enum.GetValues<ResidencyClass>())
        {
            Assert.InRange(
                ledger.Used(residencyClass),
                0,
                ledger.Budget(residencyClass));
        }
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

        throw new TimeoutException("The staged page streamer did not reach the expected residency state.");
    }

    private static void CompletePublication(ClusterMeshes manager)
    {
        Assert.True(manager.SubmitPending());
    }

    private sealed class ShortAllocationStorage : IClusterPageStorage
    {
        private byte[]? _bytes;
        private int _failNextRelease = 1;

        public Memory<byte> Allocate(uint offset, int length)
        {
            _bytes = new byte[length];
            return _bytes.AsMemory(0, length - 1);
        }

        public void Stage(uint offset, int length)
        {
        }

        public ClusterStagedUpload[] CaptureStagedUploads() => [];

        public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
        {
        }

        public void Release(uint offset, int length)
        {
            if (Interlocked.Exchange(ref _failNextRelease, 0) != 0)
                throw new InvalidOperationException("Injected staging-storage release failure.");
            _bytes = null;
        }

        public void Dispose() => _bytes = null;
    }

    private static Mesh Mesh(int pageCount)
    {
        if (pageCount is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(pageCount));

        ClusterBVHNode[] nodes = pageCount == 1
            ? [Leaf(0)]
            : [Leaf(0), Leaf(1), new ClusterBVHNode { ChildPointer = 0, ChildCount = 2, NodeType = 0 }];
        byte[] page = PageData();
        int pageRegionBytes = checked(pageCount * PageBytes);
        byte[] payload = new byte[checked(pageRegionBytes + nodes.Length * Marshal.SizeOf<ClusterBVHNode>())];
        for (int index = 0; index < pageCount; index++)
            page.CopyTo(payload, index * PageBytes);
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(pageRegionBytes));

        return SomeEngine.Resources.Mesh.Create("BudgetedMesh", ClusterTestAssets.StandardVertexEncoding, payload.AsMemory(), checked((ulong)pageRegionBytes),
            new Bounds { Center = new Vec3(), Radius = 1f });
    }

    private static byte[] PageData()
        => ClusterTestAssets.CreatePageData();

    private static ClusterBVHNode Leaf(uint localPage)
    {
        var leaf = new ClusterBVHNode { ChildPointer = localPage, NodeType = 1 };
        leaf.SetLeafData(0, 1);
        return leaf;
    }
}
