using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Frame;
using Xunit.Sdk;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterResidencyPublicationTests
{
    private const uint PageAllocationBytes = 144;

    [Fact]
    public async Task PublicationSnapshotsStagingUploadsAndMergesAdjacentResidentRanges()
    {
        var stagingBytes = new byte[checked((int)(2 * PageAllocationBytes))];
        var staging = new TestClusterPageStorage(stagingBytes);
        using var manager = new ClusterMeshes(
            2 * PageAllocationBytes,
            residency: null,
            pageStorage: staging);
        ClusterMeshRegistration first = await manager.AddAuthoredMeshAsync(
            CreateAlignedSinglePageMesh("First"));
        ClusterMeshRegistration second = await manager.AddAuthoredMeshAsync(
            CreateAlignedSinglePageMesh("Second"));
        Assert.True(manager.SubmitPending());

        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, first.FirstPageId));
        Assert.Equal(PageLoadResult.Staged, await ClusterTestAssets.LoadPageAsync(manager, second.FirstPageId));

        ClusterMeshPublication publication = Assert.IsType<ClusterMeshPublication>(
            manager.PreparePublication());
        Assert.Equal(2, publication.PageUploads.Length);
        Assert.Equal(new ulong[] { 0, PageAllocationBytes },
            publication.PageUploads.Select(static upload => upload.Offset));
        Assert.All(publication.PageUploads,
            static upload => Assert.Equal(checked((int)PageAllocationBytes), upload.Bytes.Length));
        Assert.Equal(
            [new BufferRange(0, 2 * PageAllocationBytes)],
            publication.ResidentPageRanges);
        Assert.Equal(2, staging.AllocationCount);

        manager.CommitPublication(publication, []);
        Assert.Equal(0, staging.AllocationCount);
        Assert.Equal(
            [new BufferRange(0, 2 * PageAllocationBytes)],
            manager.CaptureResidentPageRanges());
    }

    [Fact]
    public async Task EvictedRangeCannotBeReusedUntilItsPriorReaderCompletes()
    {
        using WarpResidencyTestSupport support = WarpResidencyTestSupport.Create();
        using ExternalTimeline releaseReader = support.Backend.CreateExternalTimeline(
            support.Device,
            initialValue: 0);
        using var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("Retirement"));
        Assert.True(manager.SubmitPending());
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        Assert.True(manager.SubmitPending());

        QueueCompletion oldReader = support.Backend.Submit(
            support.GraphicsQueue,
            new QueueSubmitDesc(
                [],
                [new TimelinePoint(releaseReader, 1)],
                [],
                [],
                []));
        bool signaled = false;
        try
        {
            Assert.False(support.Backend.IsComplete(oldReader));
            Assert.True(manager.EvictPage(registration.FirstPageId));
            ClusterMeshPublication retirement = Assert.IsType<ClusterMeshPublication>(
                manager.PreparePublication());
            manager.CommitPublication(retirement, [[oldReader]]);

            ClusterMeshesSnapshot held = manager.CaptureSnapshot();
            Assert.Equal(0u, held.Pages.Resident);
            Assert.Equal(1, held.Pages.UncompletedEvictions);
            Assert.Equal(PageAllocationBytes, held.Heap.UsedBytes);
            Assert.Equal(PageAllocationBytes, held.Residency.GpuUsedBytes);
            Assert.Equal(
                PageLoadResult.Deferred,
                await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));

            manager.RetireCompleted(support.Backend);
            Assert.Equal(PageAllocationBytes, manager.CaptureSnapshot().Heap.UsedBytes);

            QueueCompletion signal = support.Backend.Submit(
                support.ComputeQueue,
                new QueueSubmitDesc(
                    [],
                    [],
                    [],
                    [],
                    [new TimelineSignal(releaseReader, 1)]));
            signaled = true;
            support.Wait(signal);
            support.Wait(oldReader);
            manager.RetireCompleted(support.Backend);

            ClusterMeshesSnapshot retired = manager.CaptureSnapshot();
            Assert.Equal(0, retired.Pages.UncompletedEvictions);
            Assert.Equal(0u, retired.Heap.UsedBytes);
            Assert.Equal(0, retired.Residency.GpuUsedBytes);
            Assert.Equal(
                PageLoadResult.Staged,
                await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        }
        finally
        {
            if (!signaled)
            {
                QueueCompletion signal = support.Backend.Submit(
                    support.ComputeQueue,
                    new QueueSubmitDesc(
                        [],
                        [],
                        [],
                        [],
                        [new TimelineSignal(releaseReader, 1)]));
                support.Wait(signal);
                support.Wait(oldReader);
            }
        }
    }

    [Fact]
    public async Task FrozenEvictionCannotBeCancelledWhilePublicationAwaitsSubmission()
    {
        using var manager = new ClusterMeshes(PageAllocationBytes);
        ClusterMeshRegistration registration = await manager.AddAuthoredMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("Frozen retirement"));
        Assert.True(manager.SubmitPending());
        Assert.Equal(
            PageLoadResult.Staged,
            await ClusterTestAssets.LoadPageAsync(manager, registration.FirstPageId));
        Assert.True(manager.SubmitPending());
        Assert.True(manager.EvictPage(registration.FirstPageId));

        ClusterMeshPublication publication = Assert.IsType<ClusterMeshPublication>(
            manager.PreparePublication());
        Assert.Equal(
            PageFaultResolutionKind.Pending,
            manager.ResolvePage(registration.FirstPageId).Kind);

        manager.CommitPublication(publication, [[]]);
        ClusterMeshesSnapshot committed = manager.CaptureSnapshot();
        Assert.Equal(0u, committed.Pages.Resident);
        Assert.Equal(0, committed.Pages.UncompletedEvictions);
    }

    [Fact]
    public void ResidencyShaderBuffersAreDeviceLocalCopyDestinations()
    {
        using WarpResidencyTestSupport support = WarpResidencyTestSupport.Create();
        using var renderDevice = new RenderDevice(support.Backend, support.Device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        RenderTimeline timeline = coordinator.CreateTimeline(generationCount: 1);
        using var residency = new ClusterResidency(
            support.Backend,
            support.Device,
            timeline,
            new ClusterResidencyOptions
            {
                PageHeapCapacityBytes = PageAllocationBytes,
                BvhCapacityBytes = 4 * 1024,
                PageFaultCapacity = 32,
            });

        ClusterResidencyBinding binding = residency.GetBinding();
        Assert.Equal(MemoryType.DeviceLocal, binding.PageHeap.Info.MemoryType);
        Assert.Equal(MemoryType.DeviceLocal, binding.Bvh.Info.MemoryType);
        Assert.Equal(
            BufferUsages.ShaderRead | BufferUsages.CopyDestination,
            binding.PageHeap.Info.Usages);
        Assert.Equal(
            BufferUsages.ShaderRead | BufferUsages.CopyDestination,
            binding.Bvh.Info.Usages);
    }

    private static Mesh CreateAlignedSinglePageMesh(string name)
    {
        byte[] page = new byte[PageAllocationBytes];
        ClusterTestAssets.CreatePageData().CopyTo(page, 0);
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
        header.ExtensionDataSize = checked(PageAllocationBytes - header.ExtensionDataOffset);
        MemoryMarshal.Write(page, in header);

        int bvhBytes = Marshal.SizeOf<ClusterBVHNode>();
        byte[] payload = new byte[checked(page.Length + bvhBytes)];
        page.CopyTo(payload, 0);
        var leaf = new ClusterBVHNode
        {
            ChildPointer = 0,
            NodeType = 1,
        };
        leaf.SetLeafData(clusterStart: 0, clusterCount: 1);
        MemoryMarshal.Write(payload.AsSpan(page.Length, bvhBytes), in leaf);
        return new Mesh
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1 },
            Payload = payload,
            VertexEncoding = ClusterTestAssets.StandardVertexEncoding,
            BvhOffset = PageAllocationBytes,
        };
    }

    private sealed class WarpResidencyTestSupport : IDisposable
    {
        private WarpResidencyTestSupport(
            IGraphicsBackend backend,
            Device device,
            Queue graphicsQueue,
            Queue computeQueue)
        {
            Backend = backend;
            Device = device;
            GraphicsQueue = graphicsQueue;
            ComputeQueue = computeQueue;
        }

        internal IGraphicsBackend Backend { get; }
        internal Device Device { get; }
        internal Queue GraphicsQueue { get; }
        internal Queue ComputeQueue { get; }

        internal static WarpResidencyTestSupport Create()
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("D3D12 WARP requires Windows.");
            IGraphicsBackend backend = D3D12GraphicsBackend.Create();
            try
            {
                AdapterEnumerationOptions options = new(
                    AdapterPreference.HighPerformance,
                    IncludeSoftware: true);
                _ = backend.TryEnumerateAdapters(options, [], out int count);
                var adapters = new AdapterInfo[count];
                if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
                    confirmed != adapters.Length)
                {
                    throw new InvalidOperationException("The adapter set changed during enumeration.");
                }
                AdapterInfo warp = adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated);
                if (warp.Name.Length == 0)
                    throw SkipException.ForSkip("The D3D12 WARP adapter is unavailable.");
                Device device = backend.CreateDevice(new DeviceDesc(
                    warp.Id,
                    [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)],
                    optionalFeatures: DeviceFeatures.ExternalTimelines,
                    label: "Cluster residency WARP test"));
                return new WarpResidencyTestSupport(
                    backend,
                    device,
                    backend.GetQueue(device, QueueType.Graphics),
                    backend.GetQueue(device, QueueType.Compute));
            }
            catch
            {
                backend.Dispose();
                throw;
            }
        }

        internal void Wait(in QueueCompletion completion)
        {
            if (Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                throw new TimeoutException("A WARP Queue did not complete within ten seconds.");
        }

        public void Dispose()
        {
            Backend.CollectCompleted(Device);
            Device.Dispose();
            Backend.Dispose();
        }
    }
}
