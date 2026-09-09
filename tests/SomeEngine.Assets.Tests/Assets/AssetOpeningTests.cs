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
        Task<Asset<ReadingSample>> firstRead = loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Asset<ReadingSample>> secondRead = loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask();
        storage.ReleaseReads();
        await using Asset<ReadingSample> first = await firstRead;
        await using Asset<ReadingSample> second = await secondRead;
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
        Task<Asset<ReadingSample>> first = loader.OpenAsync(new AssetId<ReadingSample>(id), cancel.Token).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Asset<ReadingSample>> second = loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        storage.ReleaseReads();
        await using Asset<ReadingSample> file = await second;
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
        await using Asset<ReadingSample> old = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        using ReadingSample value = ReadingSample.Create(old);
        storage.Publish(id, directory.WriteSample(id, 2, "new"));
        await using Asset<ReadingSample> current = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        Assert.NotEqual(old.Version, current.Version);
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
        await using Asset<ReadingSample> first = await AssetLoader.OpenFileAsync<ReadingSample>(directory.WriteSample(id, 8));
        using ReadingSample value = ReadingSample.Create(first);
        await using Asset<ReadingSample> invalid = await AssetLoader.OpenFileAsync<ReadingSample>(directory.WriteSample(id, -1, "invalid"));
        Assert.Throws<InvalidDataException>(() => value.Update(invalid));
        Assert.Equal(8, value.Number);
        await using Asset<ReadingSample> repaired = await AssetLoader.OpenFileAsync<ReadingSample>(directory.WriteSample(id, 9, "repaired"));
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
        Task<Asset<ReadingSample>> read = loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loader.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask());
        Assert.Equal(0, storage.StorageClosed);
        Assert.Equal(1, storage.RangesClosed);
    }
}
