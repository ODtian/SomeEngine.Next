using SomeEngine.Serialization;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

[Collection("Asset samples")]
public sealed class AssetTypeExtensibilityTests
{
    [Fact]
    public async Task DownstreamFormatCreatesIndependentDomainObjects()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var project = new AssetProject(directory.Path, []);
        using ReadingSampleAsset written = new ReadingSampleAsset(id) { Name = "third-party" };
        using var content = new ReadingSampleBlock { Number = 42 };
        written.SetBlock(content);
        project.RegisterAsset(await written.SaveAsync(directory.File("new.reading.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        await using ReadingSampleAsset file = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        ReadingSampleBlock opened = await file.GetBlockAsync<ReadingSampleBlock>();
        using ReadingSample first = opened.Create(file.Name);
        using ReadingSample second = opened.Create(file.Name);
        Assert.NotSame(first, second);
        Assert.Equal(42, first.Number);
        first.Dispose();
        Assert.False(second.IsDisposed);
        await loader.DisposeAsync();
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public async Task ExistingFileRebuildsTheIndexThroughItsGeneratedFormat()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        string path = directory.WriteSample(id, 7);
        var project = new AssetProject(directory.Path, []);
        using var opened = await ReadingSampleAsset.OpenAsync(path);
        Assert.Equal(id, project.RegisterAsset(opened.Describe()));
        Assert.Equal(id, project.Resolve(path));
        Assert.Single(project.Manifest.Assets);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.asset.meta"));
    }

    [Fact]
    public async Task ThirdPartyStorageProvidesBytesWithoutDecodingOrOwningObjects()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage();
        storage.Publish(id, directory.WriteSample(id, 13));
        await using var loader = new AssetLoader(storage);
        await using ReadingSampleAsset file = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        using ReadingSample value = (await file.GetBlockAsync<ReadingSampleBlock>()).Create(file.Name);
        await loader.DisposeAsync();
        Assert.Equal(13, value.Number);
        Assert.False(value.IsDisposed);
        Assert.Equal(0, storage.StorageClosed);
        Assert.Equal(0, storage.RangesClosed);
        await file.DisposeAsync();
        Assert.Equal(1, storage.RangesClosed);
    }

    [Fact]
    public async Task DependenciesAreEnumeratedWithoutBeingInstantiated()
    {
        using var directory = new AssetTestDirectory();
        Guid dependency = Guid.NewGuid();
        using ReadingSampleAsset written = new ReadingSampleAsset();
        using var content = new ReadingSampleBlock { Dependency = new(dependency) };
        written.SetBlock(content);
        var project = new AssetProject(directory.Path, []);
        Guid id = project.RegisterAsset(await written.SaveAsync(directory.File("parent.reading.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        await using ReadingSampleAsset file = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        Assert.Equal([dependency], file.Describe().Dependencies);
        Assert.Equal([id], project.Manifest.GetReferencers(dependency));
        Assert.Equal("sample", (await file.GetBlockAsync<ReadingSampleBlock>()).Create(file.Name).Name);
    }

    [Fact]
    public async Task WrongTypeAndSchemaAreRejectedBeforeOpeningBytes()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage();
        string path = directory.WriteSample(id, 1);
        storage.Publish(id, path, type: TextureAsset.FileType);
        await using var loader = new AssetLoader(storage);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask());
        Assert.Equal(0, storage.Opens);
        storage.Publish(id, path, schema: ReadingSampleAsset.SchemaHash ^ 1UL);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask());
        Assert.Equal(0, storage.Opens);
    }

    [Fact]
    public async Task CorruptContentReleasesItsRangeAndCanBeReopenedAfterRepair()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        string path = directory.WriteSample(id, 11);
        var storage = new ReadingStorage();
        storage.Publish(id, path, corrupt: true);
        await using var loader = new AssetLoader(storage);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id)).AsTask());
        Assert.Equal(1, storage.RangesClosed);
        storage.Publish(id, path);
        await using ReadingSampleAsset file = await ReadingSampleAsset.OpenAsync(loader, new AssetId<ReadingSampleAsset>(id));
        Assert.Equal(11, (await file.GetBlockAsync<ReadingSampleBlock>()).Create(file.Name).Number);
    }
}
