namespace SomeEngine.Assets.Tests.Assets;

public sealed class ResidentAssetTableTests
{
    [Fact]
    public async Task ConcurrentLoadsPublishOneCanonicalObject()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var publication = new TaskCompletionSource<AssetPublication<Probe>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;

        ValueTask<Probe> first = table.LoadAsync(
            guid,
            (_, _) =>
            {
                Interlocked.Increment(ref loadCount);
                return publication.Task;
            },
            default);
        ValueTask<Probe> second = table.LoadAsync<Probe>(
            guid,
            (_, _) => throw new InvalidOperationException("single-flight was not reused"),
            default);

        var expected = new Probe(11);
        publication.SetResult(new AssetPublication<Probe>(expected));

        Assert.Same(expected, await first);
        Assert.Same(expected, await second);
        Assert.Equal(1, loadCount);
        Assert.True(table.TryFind(guid, out Probe? found));
        Assert.Same(expected, found);
        Assert.True(table.TryGetAssetGuid(expected, out AssetGuid foundGuid));
        Assert.Equal(guid, foundGuid);
        Assert.Equal<ulong>(1, table.GetRevision(expected));
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedPublication()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var publication = new TaskCompletionSource<AssetPublication<Probe>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();

        ValueTask<Probe> canceledCaller = table.LoadAsync(
            guid,
            (_, operationCancellation) =>
            {
                Assert.False(operationCancellation.IsCancellationRequested);
                return publication.Task;
            },
            callerCancellation.Token);
        ValueTask<Probe> survivingCaller = table.LoadAsync<Probe>(
            guid,
            (_, _) => throw new InvalidOperationException("single-flight was not reused"),
            default);

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledCaller.AsTask());

        var expected = new Probe(23);
        publication.SetResult(new AssetPublication<Probe>(expected));
        Assert.Same(expected, await survivingCaller);
    }

    [Fact]
    public async Task ReloadMutatesCanonicalObjectAndAdvancesRevision()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var canonical = new Probe(3);
        Probe loaded = await table.LoadAsync(
            guid,
            (_, _) => Task.FromResult(new AssetPublication<Probe>(canonical)),
            default);
        var replacement = new Probe(37);

        Probe reloaded = await table.ReloadAsync(
            loaded,
            (_, _) => Task.FromResult(new AssetPublication<Probe>(replacement)),
            static (current, incoming, _) =>
            {
                current.Value = incoming.Value;
                return ValueTask.CompletedTask;
            },
            default);

        Assert.Same(canonical, reloaded);
        Assert.Equal(37, canonical.Value);
        Assert.Equal<ulong>(2, table.GetRevision(canonical));
    }

    [Fact]
    public async Task FailedReloadKeepsCanonicalObjectAndRevision()
    {
        await using var table = new ResidentAssetTable();
        AssetGuid guid = AssetGuid.New();
        var canonical = new Probe(5);
        Probe loaded = await table.LoadAsync(
            guid,
            (_, _) => Task.FromResult(new AssetPublication<Probe>(canonical)),
            default);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => table.ReloadAsync(
                loaded,
                (_, _) => Task.FromException<AssetPublication<Probe>>(
                    new InvalidDataException("broken publication")),
                static (_, _, _) => ValueTask.CompletedTask,
                default).AsTask());

        Assert.Same(canonical, loaded);
        Assert.Equal(5, canonical.Value);
        Assert.Equal<ulong>(1, table.GetRevision(canonical));
    }

    [Fact]
    public async Task DisposalReleasesCanonicalObjectsInReversePublicationOrder()
    {
        var order = new List<int>();
        var table = new ResidentAssetTable();
        DisposableProbe first = await table.LoadAsync(
            AssetGuid.New(),
            (_, _) => Task.FromResult(
                new AssetPublication<DisposableProbe>(new DisposableProbe(1, order))),
            default);
        DisposableProbe second = await table.LoadAsync(
            AssetGuid.New(),
            (_, _) => Task.FromResult(
                new AssetPublication<DisposableProbe>(new DisposableProbe(2, order))),
            default);

        Assert.NotSame(first, second);
        await table.DisposeAsync();

        Assert.Equal([2, 1], order);
    }

    private sealed class Probe(int value)
    {
        internal int Value { get; set; } = value;
    }

    private sealed class DisposableProbe(int id, List<int> order) : IDisposable
    {
        public void Dispose() => order.Add(id);
    }
}
