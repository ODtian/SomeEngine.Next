using System.Runtime.CompilerServices;
using SomeEngine.Assets;

namespace SomeEngine.Tests.Assets;

public sealed class ResidentAssetTableTests
{
    [Fact]
    public async Task DefaultHandle_IsInvalidAndRejectedByEveryTable()
    {
        await using var table = new ResidentAssetTable();
        AssetHandle<object> handle = default;

        Assert.False(handle.IsValid);
        Assert.Equal(AssetLoadState.Invalid, handle.LoadState);
        Assert.False(table.TryRead(handle, out _));
        Assert.Throws<InvalidOperationException>(() => table.Read(handle));
    }

    [Fact]
    public async Task Load_ReturnsCanonicalStrongHandleBeforeSharedIoCompletes()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var source = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        AssetHandle<string> first = table.Load(
            guid,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return source.Task;
            });
        AssetHandle<string> second = table.Load(
            guid,
            static (_, _) => Task.FromResult<string?>("duplicate"));

        Assert.Equal(first, second);
        Assert.Equal(new AssetId<string>(guid), first.AssetId);
        Assert.Equal(AssetLoadState.Loading, first.LoadState);
        Assert.False(table.TryRead(first, out _));
        Assert.Equal(1, calls);

        source.SetResult("loaded");
        await table.WaitAsync(first, default);
        await table.WaitAsync(second, default);

        Assert.Equal(AssetLoadState.Ready, first.LoadState);
        Assert.Equal<ulong>(1, first.Revision);
        using AssetRead<string> firstRead = table.Read(first);
        using AssetRead<string> secondRead = table.Read(second);
        Assert.Same(firstRead.Value, secondRead.Value);
        Assert.Equal("loaded", firstRead.Value);
    }

    [Fact]
    public async Task Handle_IsAffineToItsOwningTable()
    {
        await using var firstTable = new ResidentAssetTable();
        await using var secondTable = new ResidentAssetTable();
        AssetHandle<object> handle = firstTable.Load(
            AssetGuid.New(),
            static (_, _) => Task.FromResult<object?>(new object()));
        await firstTable.WaitAsync(handle, default);

        Assert.True(firstTable.TryRead(handle, out AssetRead<object>? read));
        read!.Dispose();
        Assert.False(secondTable.TryRead(handle, out _));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondTable.WaitAsync(handle, default).AsTask());
    }

    [Fact]
    public async Task WaiterCancellation_DoesNotCancelSharedLoad()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sharedToken = default;
        using var waiterCancellation = new CancellationTokenSource();

        AssetHandle<string> handle = table.Load(
            guid,
            (_, token) =>
            {
                sharedToken = token;
                started.SetResult();
                return release.Task;
            });
        await started.Task;
        Task<AssetHandle<string>> cancelled = table
            .WaitAsync(handle, waiterCancellation.Token).AsTask();
        Task<AssetHandle<string>> surviving = table.WaitAsync(handle, default).AsTask();

        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(sharedToken.IsCancellationRequested);
        release.SetResult("ready");

        Assert.Equal(handle, await surviving);
        using AssetRead<string> read = table.Read(handle);
        Assert.Equal("ready", read.Value);
    }

    [Fact]
    public async Task FailedLoad_RemainsObservableThroughTheCanonicalHandle()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var failure = new InvalidDataException("broken current asset");
        AssetHandle<string> handle = table.Load(
            guid,
            (_, _) => Task.FromException<string?>(failure));

        Exception observed = await Assert.ThrowsAsync<InvalidDataException>(
            () => table.WaitAsync(handle, default).AsTask());

        Assert.Same(failure, observed);
        Assert.Equal(AssetLoadState.Failed, handle.LoadState);
        Assert.Same(failure, handle.Failure);
        Assert.Equal(handle, table.Load(
            guid,
            static (_, _) => Task.FromResult<string?>("must not retry implicitly")));
        Assert.False(table.TryRead(handle, out _));
    }

    [Fact]
    public async Task ReloadAsync_DrainsReadsAndDestroysOldValueBeforeOpeningReplacement()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var original = new DisposableAsset();
        var replacement = new DisposableAsset();
        AssetHandle<DisposableAsset> handle = table.Load(
            guid,
            (_, _) => Task.FromResult<DisposableAsset?>(original));
        await table.WaitAsync(handle, default);
        AssetRead<DisposableAsset> read = table.Read(handle);
        int replacementLoads = 0;

        Task<AssetHandle<DisposableAsset>> reloading = table.ReloadAsync(
            handle,
            (_, _) =>
            {
                Assert.Equal(1, original.DisposeCount);
                Interlocked.Increment(ref replacementLoads);
                return Task.FromResult(new AssetPublication<DisposableAsset>(replacement, []));
            },
            default).AsTask();

        await Task.Yield();
        Assert.Equal(AssetLoadState.Loading, handle.LoadState);
        Assert.Equal(0, original.DisposeCount);
        Assert.Equal(0, replacementLoads);
        Assert.False(reloading.IsCompleted);

        read.Dispose();
        Assert.Equal(handle, await reloading);
        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(1, replacementLoads);
        Assert.Equal<ulong>(2, handle.Revision);
        using AssetRead<DisposableAsset> replacementRead = table.Read(handle);
        Assert.Same(replacement, replacementRead.Value);
    }

    [Fact]
    public async Task ConcurrentReloads_JoinOneReplacementAttempt()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        AssetHandle<string> handle = table.Load(
            guid,
            static (_, _) => Task.FromResult<string?>("first"));
        await table.WaitAsync(handle, default);
        var replacement = new TaskCompletionSource<AssetPublication<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<AssetHandle<string>> first = table.ReloadAsync(
            handle,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return replacement.Task;
            },
            default).AsTask();
        Task<AssetHandle<string>> second = table.ReloadAsync(
            handle,
            static (_, _) => throw new Xunit.Sdk.XunitException(
                "A concurrent reload must not start another load callback."),
            default).AsTask();

        replacement.SetResult(new AssetPublication<string>("second", []));
        Assert.Equal(handle, await first);
        Assert.Equal(handle, await second);
        Assert.Equal(1, calls);
        Assert.Equal<ulong>(2, handle.Revision);
    }

    [Fact]
    public async Task FailedReload_HasNoFallbackBackingAndCanBeRetriedOnSameHandle()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var original = new DisposableAsset();
        var replacement = new DisposableAsset();
        AssetHandle<DisposableAsset> handle = table.Load(
            guid,
            (_, _) => Task.FromResult<DisposableAsset?>(original));
        await table.WaitAsync(handle, default);
        var failure = new InvalidDataException("replacement is invalid");

        Exception observed = await Assert.ThrowsAsync<InvalidDataException>(
            () => table.ReloadAsync(
                handle,
                (_, _) => Task.FromException<AssetPublication<DisposableAsset>>(failure),
                default).AsTask());

        Assert.Same(failure, observed);
        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(AssetLoadState.Failed, handle.LoadState);
        Assert.Equal<ulong>(1, handle.Revision);
        Assert.False(table.TryRead(handle, out _));

        AssetHandle<DisposableAsset> retried = await table.ReloadAsync(
            handle,
            (_, _) => Task.FromResult(new AssetPublication<DisposableAsset>(replacement, [])),
            default);
        Assert.Equal(handle, retried);
        Assert.Equal(AssetLoadState.Ready, handle.LoadState);
        Assert.Equal<ulong>(2, handle.Revision);
    }

    [Fact]
    public async Task ReacquireAfterRetirement_WaitsForOldBackingToBeReleasedBeforeIo()
    {
        using var lifetime = new CancellationTokenSource();
        var set = new ResidentAssetSet<BlockingDisposeAsset>(tableIdentity: 77, lifetime.Token);
        AssetGuid guid = AssetGuid.New();
        var original = new BlockingDisposeAsset();
        AssetHandle<BlockingDisposeAsset> first = set.Load(
            guid,
            (_, _) => Task.FromResult(
                new AssetPublication<BlockingDisposeAsset>(original, [])));
        await set.WaitAsync(first, default);
        AssetHandleState<BlockingDisposeAsset> state = first.Reference!;
        AssetRetirement<BlockingDisposeAsset> retirement = await state.UnloadAsync(
            new ObjectDisposedException("released test handle"));
        set.RetireFromFinalizer(state, retirement);
        await original.DisposeStarted.Task;
        int replacementLoads = 0;
        var replacement = new BlockingDisposeAsset();

        AssetHandle<BlockingDisposeAsset> second = set.Load(
            guid,
            (_, _) =>
            {
                Interlocked.Increment(ref replacementLoads);
                return Task.FromResult(new AssetPublication<BlockingDisposeAsset>(
                    replacement,
                    []));
            });

        await Task.Yield();
        Assert.Equal(0, replacementLoads);
        Assert.Equal(AssetLoadState.Loading, second.LoadState);

        original.AllowDispose.SetResult();
        await set.WaitAsync(second, default);
        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(1, replacementLoads);
        ValueTask disposing = set.DisposeAsync();
        await replacement.DisposeStarted.Task;
        replacement.AllowDispose.SetResult();
        await disposing;
    }

    [Fact]
    public async Task DisposeAsync_WaitsForActiveReadBeforeDestroyingUniqueValue()
    {
        var table = new ResidentAssetTable();
        var asset = new DisposableAsset();
        AssetHandle<DisposableAsset> handle = table.Load(
            AssetGuid.New(),
            (_, _) => Task.FromResult<DisposableAsset?>(asset));
        await table.WaitAsync(handle, default);
        AssetRead<DisposableAsset> read = table.Read(handle);

        Task disposing = table.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, asset.DisposeCount);
        Assert.Equal(AssetLoadState.Unloaded, handle.LoadState);

        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = read.Value);
        await disposing;
        Assert.Equal(1, asset.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsSharedLoad()
    {
        var table = new ResidentAssetTable();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AssetHandle<string> handle = table.Load(
            AssetGuid.New(),
            async (_, token) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "unreachable";
                }
                finally
                {
                    exited.SetResult();
                }
            });
        await started.Task;
        Task<AssetHandle<string>> wait = table.WaitAsync(handle, default).AsTask();

        await table.DisposeAsync();

        await exited.Task;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wait);
        Assert.Equal(AssetLoadState.Unloaded, handle.LoadState);
    }

    [Fact]
    public async Task DisposeAsync_DestroysLateResultExactlyOnce()
    {
        var table = new ResidentAssetTable();
        var source = new TaskCompletionSource<DisposableAsset?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new DisposableAsset();
        AssetHandle<DisposableAsset> handle = table.Load(
            AssetGuid.New(),
            (_, _) => source.Task);
        Task<AssetHandle<DisposableAsset>> wait = table.WaitAsync(handle, default).AsTask();

        ValueTask disposing = table.DisposeAsync();
        source.SetResult(late);
        await disposing;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => wait);
        Assert.Equal(1, late.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DestroysValueWhoseDependencyCannotBePublished()
    {
        var table = new ResidentAssetTable();
        var dependency = new DisposableAsset();
        AssetHandle<DisposableAsset> dependencyHandle = table.Load(
            AssetGuid.New(),
            (_, _) => Task.FromResult<DisposableAsset?>(dependency));
        await table.WaitAsync(dependencyHandle, default);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AssetPublication<DisposableAsset>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unpublished = new DisposableAsset();
        AssetHandle<DisposableAsset> parentHandle = table.Load<DisposableAsset>(
            AssetGuid.New(),
            (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            });
        await started.Task;
        Task<AssetHandle<DisposableAsset>> parentWait = table
            .WaitAsync(parentHandle, default)
            .AsTask();

        ValueTask disposing = table.DisposeAsync();
        release.SetResult(new AssetPublication<DisposableAsset>(
            unpublished,
            [dependencyHandle.Reference!]));
        await disposing;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => parentWait);
        Assert.Equal(1, unpublished.DisposeCount);
        Assert.Equal(1, dependency.DisposeCount);
    }

    [Fact]
    public async Task StrongHandle_IsTheOnlyReadyValueRootAfterLoadCompletes()
    {
        var table = new ResidentAssetTable();
        var asset = new DisposableAsset();
        WeakReference state = await LoadAndReleaseHandleAsync(table, asset);

        for (int attempt = 0; attempt < 8 && state.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }

        Assert.False(state.IsAlive);
        await table.DisposeAsync();
        Assert.Equal(1, asset.DisposeCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndReleaseHandleAsync(
        ResidentAssetTable table,
        DisposableAsset asset)
    {
        AssetHandle<DisposableAsset> handle = table.Load(
            AssetGuid.New(),
            (_, _) => Task.FromResult<DisposableAsset?>(asset));
        await table.WaitAsync(handle, default);
        return new WeakReference(handle.Reference!);
    }

    private sealed class DisposableAsset : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class BlockingDisposeAsset : IAsyncDisposable
    {
        internal TaskCompletionSource DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowDispose { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await AllowDispose.Task;
            DisposeCount++;
        }
    }
}
