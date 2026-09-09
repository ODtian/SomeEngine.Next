using SomeEngine.Serialization;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

[Collection("Asset samples")]
public sealed class AssetTypeExtensibilityTests
{
    [Fact]
    public async Task DownstreamFormatOpensWithoutCreatingItsDomainType()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var project = new AssetProject(directory.Path, []);
        using Asset<ReadingSample> written = new ReadingSampleData { AssetGuid = id.ToString(), Name = "third-party", Number = 42 }.ToAsset();
        project.CreateAsset("new.reading.asset", written);
        int created = ReadingSample.Created;
        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<ReadingSample> file = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        Assert.Equal(created, ReadingSample.Created);
        using ReadingSample first = ReadingSample.Create(file);
        using ReadingSample second = ReadingSample.Create(file);
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
        Assert.Equal(id, await project.RegisterAssetAsync<ReadingSample>(path));
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
        await using Asset<ReadingSample> file = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        using ReadingSample value = ReadingSample.Create(file);
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
        using Asset<ReadingSample> written = new ReadingSampleData { Dependency = dependency }.ToAsset();
        var project = new AssetProject(directory.Path, []);
        Guid id = project.CreateAsset("parent.reading.asset", written);
        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<ReadingSample> file = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        Assert.Equal([dependency], file.Dependencies);
        Assert.Equal([id], project.Manifest.GetReferencers(dependency));
        Assert.Equal("sample", ReadingSample.Create(file).Name);
    }

    [Fact]
    public async Task WrongTypeAndSchemaAreRejectedBeforeOpeningBytes()
    {
        using var directory = new AssetTestDirectory();
        Guid id = Guid.NewGuid();
        var storage = new ReadingStorage();
        string path = directory.WriteSample(id, 1);
        storage.Publish(id, path, type: Asset<Texture>.FileType);
        await using var loader = new AssetLoader(storage);
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask());
        Assert.Equal(0, storage.Opens);
        storage.Publish(id, path, schema: Asset<ReadingSample>.SchemaHash ^ 1UL);
        await Assert.ThrowsAsync<BinarySchemaMismatchException>(() => loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask());
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
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => loader.OpenAsync(new AssetId<ReadingSample>(id)).AsTask());
        Assert.Equal(1, storage.RangesClosed);
        storage.Publish(id, path);
        await using Asset<ReadingSample> file = await loader.OpenAsync(new AssetId<ReadingSample>(id));
        Assert.Equal(11, ReadingSample.Create(file).Number);
    }
}
