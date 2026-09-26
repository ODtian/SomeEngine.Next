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
        Task<ReadingSampleAsset> firstRead = ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ReadingSampleAsset> secondRead = ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask();
        storage.ReleaseReads();
        await using ReadingSampleAsset first = await firstRead;
        await using ReadingSampleAsset second = await secondRead;
        Assert.NotSame(first, second);
        Assert.Equal(1, storage.Opens);
        await first.DisposeAsync();
        Assert.Equal(4, (await second.GetBlockAsync<ReadingSampleBlock>()).Create(second.Name).Number);
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
        Task<ReadingSampleAsset> first = ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id), cancel.Token).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ReadingSampleAsset> second = ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        storage.ReleaseReads();
        await using ReadingSampleAsset file = await second;
        Assert.Equal(5, (await file.GetBlockAsync<ReadingSampleBlock>()).Create(file.Name).Number);
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
        await using ReadingSampleAsset old = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        using ReadingSample value = (await old.GetBlockAsync<ReadingSampleBlock>()).Create(old.Name);
        storage.Publish(id, directory.WriteSample(id, 2, "new"));
        await using ReadingSampleAsset current = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        Assert.NotEqual(old.SourceVersion, current.SourceVersion);
        Assert.Equal(1, (await old.GetBlockAsync<ReadingSampleBlock>()).Create(old.Name).Number);
        Assert.Equal(1, value.Number);
        (await current.GetBlockAsync<ReadingSampleBlock>()).Update(value, current.Name);
        Assert.Equal(2, value.Number);
    }

    [Fact]
    public async Task FailedDomainUpdateKeepsOldContentAndCanBeRetried()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        await using ReadingSampleAsset first = await ReadingSampleAsset.OpenAsync(directory.WriteSample(id, 8));
        using ReadingSample value = (await first.GetBlockAsync<ReadingSampleBlock>()).Create(first.Name);
        await using ReadingSampleAsset invalid = await ReadingSampleAsset.OpenAsync(directory.WriteSample(id, -1, "invalid"));
        ReadingSampleBlock invalidContent = await invalid.GetBlockAsync<ReadingSampleBlock>();
        Assert.Throws<InvalidDataException>(() => invalidContent.Update(value, invalid.Name));
        Assert.Equal(8, value.Number);
        await using ReadingSampleAsset repaired = await ReadingSampleAsset.OpenAsync(directory.WriteSample(id, 9, "repaired"));
        (await repaired.GetBlockAsync<ReadingSampleBlock>()).Update(value, repaired.Name);
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
        Task<ReadingSampleAsset> read = ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask();
        await storage.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loader.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask());
        Assert.Equal(0, storage.StorageClosed);
        Assert.Equal(1, storage.RangesClosed);
    }
}
