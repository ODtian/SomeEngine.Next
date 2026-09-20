using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterRegistrationTests
{
    private static readonly int BvhBytes = Marshal.SizeOf<ClusterBVHNode>();

    [Fact]
    public async Task ConcurrentRegistrationCopiesReadyBvhOnceIntoFinalStorage()
    {
        var finalBvh = new byte[BvhBytes * 2];
        var storage = new ControlledBvhStorage(finalBvh);
        using var manager = new ClusterMeshes(1024, null, null, storage);
        using var content = await OpenControlledAsync("SameHandle");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        storage.BeforeWrite = destination =>
        {
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment));
            Assert.Same(finalBvh, segment.Array);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<ClusterMeshRegistrationResult> first = Task.Run(async () =>
            await manager.RegisterMeshAsync(content.Mesh, pageSource: content.PageSource));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ClusterMeshRegistrationResult> second = manager.RegisterMeshAsync(content.Mesh, pageSource: content.PageSource).AsTask();
            await WaitUntilAsync(() => manager.ActiveRegistrationOperations == 2);
            release.Set();
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, static result => result.Added);
            Assert.Single(results, static result => !result.Added);
            Assert.Equal(1, storage.WriteCount);
            Assert.Equal(0, content.Source.TargetReadCount);
            Assert.Equal(0, manager.ActiveRegistrationOperations);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DifferentMeshesShareOneRegistrationAdmission()
    {
        var storage = new ControlledBvhStorage(new byte[BvhBytes * 4]);
        using var manager = new ClusterMeshes(1024, null, null, storage);
        using var firstMesh = await OpenControlledAsync("First");
        using var secondMesh = await OpenControlledAsync("Second");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        storage.BeforeWrite = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<ClusterMeshRegistrationResult> first = Task.Run(async () =>
            await manager.RegisterMeshAsync(firstMesh.Mesh, pageSource: firstMesh.PageSource));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<ClusterMeshRegistrationResult> second = manager.RegisterMeshAsync(secondMesh.Mesh, pageSource: secondMesh.PageSource).AsTask();
            await WaitUntilAsync(() => manager.ActiveRegistrationOperations == 2);
            Assert.Equal(1, storage.WriteCount);
            Assert.False(second.IsCompleted);
            release.Set();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, static result => Assert.True(result.Added));
            Assert.Equal(2, storage.WriteCount);
            Assert.Equal(0, firstMesh.Source.TargetReadCount);
            Assert.Equal(0, secondMesh.Source.TargetReadCount);
            Assert.Equal(0, manager.ActiveRegistrationOperations);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SourceAndDestinationFailuresReleaseAdmissionForRetry()
    {
        var storage = new ControlledBvhStorage(new byte[BvhBytes * 4]) { FailNextAllocation = true };
        using var manager = new ClusterMeshes(1024, null, null, storage);
        using var destinationFailure = await OpenControlledAsync("DestinationFailure");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.AddMeshAsync(destinationFailure.Mesh, pageSource: destinationFailure.PageSource).AsTask());
        Assert.Equal(0, storage.WriteCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using var sourceFailure = await OpenControlledAsync("SourceFailure");
        using var editable = ClusterTestAssets.CreateSinglePageMesh("changed");
        using var payload = ClusterTestFiles.ReadPayload(editable);
        payload[MeshPageHeader.Size + PackedCluster.SizeInBytes] ^= 1;
        using var changed = Mesh.Create("changed", sourceFailure.Mesh.VertexEncoding, payload, sourceFailure.Mesh.BvhOffset);
        sourceFailure.Mesh.Update(changed);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.AddMeshAsync(sourceFailure.Mesh, pageSource: sourceFailure.PageSource).AsTask());
        Assert.Equal(0, sourceFailure.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);

        using var valid = await OpenControlledAsync("Valid");
        ClusterMeshRegistration registration = await manager.AddMeshAsync(valid.Mesh, pageSource: valid.PageSource);
        Assert.Same(valid.Mesh, registration.Mesh);
        Assert.Equal(1, manager.CaptureSnapshot().RegisteredMeshCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDisposalWaitsForTheActiveCopy(bool dispose)
    {
        var storage = new ControlledBvhStorage(new byte[BvhBytes * 4]);
        using var manager = new ClusterMeshes(1024, null, null, storage);
        using var content = await OpenControlledAsync("Cancellation");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        storage.BeforeWrite = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<ClusterMeshRegistration> registration = Task.Run(async () =>
            await manager.AddMeshAsync(content.Mesh, cancellation.Token, content.PageSource));
        Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose)
            {
                Task idle = manager.StopRegistrations();
                Assert.False(idle.IsCompleted);
                closing = Task.Run(manager.Dispose);
            }
            else cancellation.Cancel();
            Assert.False(registration.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration);
            if (closing is not null)
            {
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ClusterLifecycle.Disposed, manager.CaptureSnapshot().Lifecycle);
            }
            else
            {
                storage.BeforeWrite = null;
                using var next = await OpenControlledAsync("AfterCancellation");
                _ = await manager.AddMeshAsync(next.Mesh, pageSource: next.PageSource);
            }
            Assert.Equal(0, manager.ActiveRegistrationOperations);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SameEpochReentryFailsFastWithoutASecondRead()
    {
        var storage = new ControlledBvhStorage(new byte[BvhBytes * 4]);
        using var manager = new ClusterMeshes(1024, null, null, storage);
        using var outer = await OpenControlledAsync("Outer");
        using var inner = await OpenControlledAsync("Inner");
        Exception? failure = null;
        storage.BeforeWrite = _ => failure = Record.Exception(() =>
            manager.AddMeshAsync(inner.Mesh, pageSource: inner.PageSource).AsTask().GetAwaiter().GetResult());
        var result = await manager.AddMeshAsync(outer.Mesh, pageSource: outer.PageSource);
        Assert.Same(outer.Mesh, result.Mesh);
        Assert.Contains("reenter", Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(0, outer.Source.TargetReadCount);
        Assert.Equal(0, inner.Source.TargetReadCount);
        Assert.Equal(0, manager.ActiveRegistrationOperations);
    }

    [Fact]
    public async Task ReloadKeepsThePublishedRegistrationOnItsRetainedOldSource()
    {
        using var manager = new ClusterMeshes();
        using var first = await ClusterTestAssets.OpenRuntimeMeshAsync(ClusterTestAssets.CreateSinglePageMesh("revision-one"));
        using var second = await ClusterTestAssets.OpenRuntimeMeshAsync(ClusterTestAssets.CreateSinglePageMesh("revision-two"));
        _ = await manager.AddMeshAsync(first.Mesh, pageSource: first.PageSource);
        Assert.True(manager.SubmitPending());
        Assert.True(manager.IsMeshRegistered(first.Mesh));
        Assert.True(manager.TryGetPublishedRoot(first.Mesh, out uint rootBefore));
        first.Mesh.Update(second.Mesh);
        Assert.True(manager.IsMeshRegistered(first.Mesh));
        Assert.True(manager.TryGetPublishedRoot(first.Mesh, out uint rootAfter));
        Assert.Equal(rootBefore, rootAfter);
    }

    [Fact]
    public async Task DifferentVertexEncodingsCanShareResidency()
    {
        using var manager = new ClusterMeshes();
        using Mesh standard = ClusterTestAssets.CreateSinglePageMesh("standard");
        var first = await manager.AddAuthoredMeshAsync(standard);
        using var payload = ClusterTestFiles.ReadPayload(standard);
        const string encoding = "test.vertex.other.v1";
        MeshPageHeader header = payload.Read<MeshPageHeader>(0);
        header.VertexEncodingHash = VertexEncodingCompatibility.Hash(encoding);
        payload.Write(0, header);
        using Mesh other = Mesh.Create("other", encoding, payload, standard.BvhOffset);
        var second = await manager.AddAuthoredMeshAsync(other);
        Assert.True(manager.IsMeshRegistered(first.Mesh));
        Assert.True(manager.IsMeshRegistered(second.Mesh));
    }

    private static ValueTask<ControlledRuntimeMesh> OpenControlledAsync(string name)
        => ClusterTestAssets.OpenControlledRuntimeMeshAsync(ClusterTestAssets.CreateSinglePageMesh(name), BvhBytes);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ControlledBvhStorage(Memory<byte> memory) : IClusterBvhStorage
    {
        private readonly TestClusterBvhStorage _inner = new(memory);
        private int _writes;
        internal bool FailNextAllocation;
        internal Action<Memory<byte>>? BeforeWrite;
        internal int WriteCount => Volatile.Read(ref _writes);
        public Memory<byte> Allocate(ulong offset, int length)
        {
            if (FailNextAllocation)
            {
                FailNextAllocation = false;
                throw new InvalidOperationException("Injected BVH destination allocation failure.");
            }
            Memory<byte> bytes = _inner.Allocate(offset, length);
            return new WriteMemory(bytes, () =>
            {
                Interlocked.Increment(ref _writes);
                BeforeWrite?.Invoke(bytes);
            }).Bytes;
        }
        public Memory<byte> GetRange(ulong offset, int length) => _inner.GetRange(offset, length);
        public void Stage(ulong offset, int length) => _inner.Stage(offset, length);
        public ClusterStagedUpload[] CaptureStagedUploads() => _inner.CaptureStagedUploads();
        public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads) => _inner.CompleteStagedUploads(uploads);
        public void Release(ulong offset, int length) => _inner.Release(offset, length);
        public void Dispose() => _inner.Dispose();
    }

    private sealed class WriteMemory(Memory<byte> bytes, Action beforeWrite) : MemoryManager<byte>
    {
        private int _accessed;
        internal Memory<byte> Bytes => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan()
        {
            if (Interlocked.Exchange(ref _accessed, 1) == 0) beforeWrite();
            return bytes.Span;
        }
        public override MemoryHandle Pin(int elementIndex = 0) => bytes[elementIndex..].Pin();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
