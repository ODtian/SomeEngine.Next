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
    public async Task SameMeshConcurrentRegistrationReadsOnceIntoFinalBvhStorage()
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

        Task<ClusterMeshRegistrationResult> first = manager
            .RegisterMeshAsync(controlled.Mesh)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ClusterMeshRegistrationResult> duplicate = manager
            .RegisterMeshAsync(controlled.Mesh)
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
    public async Task DifferentMeshesShareOneRegistrationAdmission()
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
            .RegisterMeshAsync(firstMesh.Mesh)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ClusterMeshRegistrationResult> second = manager
            .RegisterMeshAsync(secondMesh.Mesh)
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
            () => manager.AddMeshAsync(destinationFailure.Mesh).AsTask());
        Assert.Equal(0, destinationFailure.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using ControlledRuntimeMesh sourceFailure = await OpenControlledAsync("SourceFailure");
        sourceFailure.Source.BeforeRead = static (_, _, _) =>
            ValueTask.FromException(new IOException("Injected BVH source failure."));
        await Assert.ThrowsAsync<IOException>(
            () => manager.AddMeshAsync(sourceFailure.Mesh).AsTask());
        Assert.Equal(1, sourceFailure.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using ControlledRuntimeMesh valid = await OpenControlledAsync("Valid");
        ClusterMeshRegistration registration = await manager.AddMeshAsync(valid.Mesh);
        Assert.Same(valid.Mesh, registration.Mesh);
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
                .AddMeshAsync(cancelled.Mesh, cancellation.Token)
                .AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, manager.ActiveRegistrationOperations);

            using ControlledRuntimeMesh valid = await OpenControlledAsync("AfterCancellation");
            _ = await manager.AddMeshAsync(valid.Mesh);
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
            .AddMeshAsync(disposing.Mesh)
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
                () => manager.AddMeshAsync(inner.Mesh).AsTask());
        };

        ClusterMeshRegistration registration = await manager.AddMeshAsync(outer.Mesh);

        Assert.Same(outer.Mesh, registration.Mesh);
        InvalidOperationException error = Assert.IsType<InvalidOperationException>(reentryFailure);
        Assert.Contains("reenter", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, outer.Source.TargetReadCount);
        Assert.Equal(0, inner.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task ReloadKeepsThePublishedRegistrationOnItsRetainedOldSource()
    {
        using var manager = new ClusterMeshes();
        using Mesh first = await ClusterTestAssets.OpenRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("revision-one"));
        using Mesh second = await ClusterTestAssets.OpenRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh("revision-two"));
        _ = await manager.AddMeshAsync(first);
        Assert.True(manager.PublishPending());
        Assert.True(manager.IsMeshRegistered(first));
        Assert.True(manager.TryGetPublishedRoot(first, out uint rootBefore));

        await Mesh.ApplyReloadAsync(first, second, default);

        Assert.True(manager.IsMeshRegistered(first));
        Assert.True(manager.TryGetPublishedRoot(first, out uint rootAfter));
        Assert.Equal(rootBefore, rootAfter);
    }

    private static ValueTask<ControlledRuntimeMesh> OpenControlledAsync(string name)
        => ClusterTestAssets.OpenControlledRuntimeMeshAsync(
            ClusterTestAssets.CreateSinglePageMesh(name),
            BvhBytes);

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
