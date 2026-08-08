using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterRegistrationTests
{
    private static readonly int BvhBytes = Marshal.SizeOf<ClusterBVHNode>();

    [Fact]
    public async Task SameHandleConcurrentRegistrationReadsOnceIntoFinalBvhStorage()
    {
        var finalBvh = new byte[checked(BvhBytes * 2)];
        using var manager = new ClusterMeshes(
            pageHeapCapacity: 1024,
            residency: null,
            pageStorage: null,
            bvhStorage: new TestClusterBvhStorage(finalBvh));
        using ControlledRuntimeMesh controlled = await OpenControlledAsync("SameHandle");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controlled.Source.BeforeRead = async (_, destination, cancellationToken) =>
        {
            Assert.True(MemoryMarshal.TryGetArray(
                (ReadOnlyMemory<byte>)destination,
                out ArraySegment<byte> segment));
            Assert.Same(finalBvh, segment.Array);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        AssetHandle<Mesh> handle = MeshHandle(1);
        Task<ClusterMeshRegistrationResult> first = manager
            .RegisterMeshAsync(handle, controlled.Mesh)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ClusterMeshRegistrationResult> duplicate = manager
            .RegisterMeshAsync(handle, controlled.Mesh)
            .AsTask();
        await WaitUntilAsync(() => manager.ActiveRegistrationOperations == 2);

        release.TrySetResult();
        ClusterMeshRegistrationResult[] results = await Task.WhenAll(first, duplicate);

        Assert.Single(results, static result => result.Added);
        Assert.Single(results, static result => !result.Added);
        Assert.Equal(1, controlled.Source.TargetReadCount);
        Assert.Equal(1, manager.CaptureSnapshot().RegisteredMeshCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task DifferentHandlesShareOneRegistrationAdmission()
    {
        using var manager = new ClusterMeshes();
        using ControlledRuntimeMesh firstMesh = await OpenControlledAsync("First");
        using ControlledRuntimeMesh secondMesh = await OpenControlledAsync("Second");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstMesh.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        Task<ClusterMeshRegistrationResult> first = manager
            .RegisterMeshAsync(MeshHandle(2), firstMesh.Mesh)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ClusterMeshRegistrationResult> second = manager
            .RegisterMeshAsync(MeshHandle(3), secondMesh.Mesh)
            .AsTask();
        await WaitUntilAsync(() => manager.ActiveRegistrationOperations == 2);

        Assert.Equal(0, secondMesh.Source.TargetReadCount);
        Assert.False(second.IsCompleted);
        release.TrySetResult();

        ClusterMeshRegistrationResult[] results = await Task.WhenAll(first, second);
        Assert.All(results, static result => Assert.True(result.Added));
        Assert.Equal(1, firstMesh.Source.TargetReadCount);
        Assert.Equal(1, secondMesh.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task SourceAndDestinationFailuresReleaseAdmissionForRetry()
    {
        var storage = new FailOnceAllocationBvhStorage(
            new byte[checked(BvhBytes * 4)]);
        using var manager = new ClusterMeshes(
            pageHeapCapacity: 1024,
            residency: null,
            pageStorage: null,
            bvhStorage: storage);
        using ControlledRuntimeMesh destinationFailure = await OpenControlledAsync("DestinationFailure");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddMeshAsync(MeshHandle(4), destinationFailure.Mesh).AsTask());
        Assert.Equal(0, destinationFailure.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using ControlledRuntimeMesh sourceFailure = await OpenControlledAsync("SourceFailure");
        sourceFailure.Source.BeforeRead = static (_, _, _) =>
            ValueTask.FromException(new IOException("Injected BVH source failure."));
        await Assert.ThrowsAsync<IOException>(
            () => manager.AddMeshAsync(MeshHandle(5), sourceFailure.Mesh).AsTask());
        Assert.Equal(1, sourceFailure.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using ControlledRuntimeMesh valid = await OpenControlledAsync("Valid");
        ClusterMeshRegistration registration = await manager.AddMeshAsync(MeshHandle(6), valid.Mesh);
        Assert.Equal(MeshHandle(6), registration.Mesh);
        Assert.Equal(1, manager.CaptureSnapshot().RegisteredMeshCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task CancellationAndDisposeBothReleaseRegistrationOwnership()
    {
        using (var manager = new ClusterMeshes())
        using (ControlledRuntimeMesh cancelled = await OpenControlledAsync("Cancelled"))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelled.Source.BeforeRead = async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };
            using var cancellation = new CancellationTokenSource();
            Task<ClusterMeshRegistration> pending = manager
                .AddMeshAsync(MeshHandle(7), cancelled.Mesh, cancellation.Token)
                .AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, manager.ActiveRegistrationOperations);

            using ControlledRuntimeMesh valid = await OpenControlledAsync("AfterCancellation");
            _ = await manager.AddMeshAsync(MeshHandle(8), valid.Mesh);
        }

        var disposingManager = new ClusterMeshes();
        using ControlledRuntimeMesh disposing = await OpenControlledAsync("Disposing");
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        disposing.Source.BeforeRead = async (_, _, cancellationToken) =>
        {
            disposeEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        Task<ClusterMeshRegistration> disposedRegistration = disposingManager
            .AddMeshAsync(MeshHandle(9), disposing.Mesh)
            .AsTask();
        await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        disposingManager.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposedRegistration);
        Assert.Equal(ClusterLifecycle.Disposed, disposingManager.CaptureSnapshot().Lifecycle);
        Assert.Equal(0, disposingManager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task SameEpochReentryFailsFastWithoutASecondRead()
    {
        using var manager = new ClusterMeshes();
        using ControlledRuntimeMesh outer = await OpenControlledAsync("Outer");
        using ControlledRuntimeMesh inner = await OpenControlledAsync("Inner");
        Exception? reentryFailure = null;
        outer.Source.BeforeRead = async (_, _, _) =>
        {
            reentryFailure = await Record.ExceptionAsync(
                () => manager.AddMeshAsync(MeshHandle(11), inner.Mesh).AsTask());
        };

        ClusterMeshRegistration registration = await manager.AddMeshAsync(MeshHandle(10), outer.Mesh);

        Assert.Equal(MeshHandle(10), registration.Mesh);
        InvalidOperationException error = Assert.IsType<InvalidOperationException>(reentryFailure);
        Assert.Contains("reenter", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, outer.Source.TargetReadCount);
        Assert.Equal(0, inner.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task ReloadedMeshCannotReuseAStaleClusterEpochRegistration()
    {
        await using var assets = new ResidentAssetTable();
        using var manager = new ClusterMeshes();
        Mesh first = await ClusterTestAssets.OpenRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("revision-one"));
        Mesh second = await ClusterTestAssets.OpenRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("revision-two"));
        AssetHandle<Mesh> handle = assets.Load(
            AssetGuid.New(),
            (_, _) => Task.FromResult<Mesh?>(first));
        await assets.WaitAsync(handle, default);
        using (AssetRead<Mesh> read = assets.Read(handle))
            _ = await manager.AddMeshAsync(handle, read.Value);
        Assert.True(manager.PublishPending());
        Assert.True(manager.IsMeshRegistered(handle));

        await assets.ReloadAsync(
            handle,
            (_, _) => Task.FromResult(new AssetPublication<Mesh>(second, [])),
            default);

        Assert.Equal<ulong>(2, handle.Revision);
        InvalidOperationException registered = Assert.Throws<InvalidOperationException>(
            () => manager.IsMeshRegistered(handle));
        Assert.Contains("Recreate the Cluster residency epoch", registered.Message);
        InvalidOperationException published = Assert.Throws<InvalidOperationException>(
            () => manager.TryGetPublishedRoot(handle, out _));
        Assert.Contains("stale GPU geometry", published.Message);
    }

    private static ValueTask<ControlledRuntimeMesh> OpenControlledAsync(string name)
        => ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh(name),
            BvhBytes);

    private static AssetHandle<Mesh> MeshHandle(int id) => new(id, 1);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(1);
        }

        Assert.Fail("Cluster registration did not reach the expected admission state.");
    }

    private sealed class FailOnceAllocationBvhStorage : IClusterBvhStorage
    {
        private readonly TestClusterBvhStorage _inner;
        private int _failNextAllocation = 1;

        internal FailOnceAllocationBvhStorage(Memory<byte> memory)
            => _inner = new TestClusterBvhStorage(memory);

        public Memory<byte> Allocate(ulong offset, int length)
        {
            if (Interlocked.Exchange(ref _failNextAllocation, 0) != 0)
                throw new InvalidOperationException("Injected BVH destination allocation failure.");
            return _inner.Allocate(offset, length);
        }

        public Memory<byte> GetRange(ulong offset, int length)
            => _inner.GetRange(offset, length);

        public void Stage(ulong offset, int length)
            => _inner.Stage(offset, length);

        public void Publish()
            => _inner.Publish();

        public void Release(ulong offset, int length)
            => _inner.Release(offset, length);

        public void Dispose() => _inner.Dispose();
    }
}
