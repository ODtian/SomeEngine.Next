namespace SomeEngine.Assets.Tests.Assets;

[Collection("Asset samples")]
public sealed class AssetOpeningTests
{
    [Fact]
    public async Task ConcurrentOpensShareReadingAndCloseIndependently()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage(block: true);
        storage.Publish(id, directory.WriteSample(id, 4));
        await using var loader = new AssetLoader(storage);
        Task<ReadingSample.Asset> firstRead = ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ReadingSample.Asset> secondRead = ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id)).AsTask();
        storage.ReleaseReads();
        await using ReadingSample.Asset first = await firstRead;
        await using ReadingSample.Asset second = await secondRead;
        Assert.NotSame(first, second);
        Assert.Equal(1, storage.Opens);
        await first.DisposeAsync();
        Assert.Equal(4, ReadingSample.Create(second).Number);
        await loader.DisposeAsync();
        Assert.Equal(0, storage.RangesClosed);
        await second.DisposeAsync();
        Assert.Equal(1, storage.RangesClosed);
    }

    [Fact]
    public async Task CancelingOneOpenDoesNotCancelAnother()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage(block: true);
        storage.Publish(id, directory.WriteSample(id, 5));
        await using var loader = new AssetLoader(storage);
        using var cancel = new CancellationTokenSource();
        Task<ReadingSample.Asset> first = ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id), cancel.Token).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ReadingSample.Asset> second = ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id)).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        storage.ReleaseReads();
        await using ReadingSample.Asset file = await second;
        Assert.Equal(5, ReadingSample.Create(file).Number);
        Assert.Equal(1, storage.Opens);
    }

    [Fact]
    public async Task OldAndNewFileVersionsRemainReadableAndObjectsUpdateExplicitly()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage();
        storage.Publish(id, directory.WriteSample(id, 1, "old"));
        await using var loader = new AssetLoader(storage);
        await using ReadingSample.Asset old = await ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id));
        using ReadingSample value = ReadingSample.Create(old);
        storage.Publish(id, directory.WriteSample(id, 2, "new"));
        await using ReadingSample.Asset current = await ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id));
        Assert.NotEqual(old.SourceVersion, current.SourceVersion);
        Assert.Equal(1, ReadingSample.Create(old).Number);
        Assert.Equal(1, value.Number);
        value.Update(current);
        Assert.Equal(2, value.Number);
    }

    [Fact]
    public async Task FailedDomainUpdateKeepsOldContentAndCanBeRetried()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        await using ReadingSample.Asset first = await ReadingSample.Asset.OpenAsync(directory.WriteSample(id, 8));
        using ReadingSample value = ReadingSample.Create(first);
        await using ReadingSample.Asset invalid = await ReadingSample.Asset.OpenAsync(directory.WriteSample(id, -1, "invalid"));
        Assert.Throws<InvalidDataException>(() => value.Update(invalid));
        Assert.Equal(8, value.Number);
        await using ReadingSample.Asset repaired = await ReadingSample.Asset.OpenAsync(directory.WriteSample(id, 9, "repaired"));
        value.Update(repaired);
        Assert.Equal(9, value.Number);
    }

    [Fact]
    public async Task ClosingLoaderCancelsItsReadAndRejectsNewRequestsWithoutClosingStorage()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage(block: true);
        storage.Publish(id, directory.WriteSample(id, 0));
        var loader = new AssetLoader(storage);
        Task<ReadingSample.Asset> read = ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loader.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadingSample.Asset.OpenAsync(loader, new AssetId<ReadingSample>(id)).AsTask());
        Assert.Equal(0, storage.StorageClosed);
        Assert.Equal(1, storage.RangesClosed);
    }
}
