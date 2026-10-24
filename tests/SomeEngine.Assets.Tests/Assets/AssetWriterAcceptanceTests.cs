namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetWriterAcceptanceTests
{
    [Fact]
    public async Task SaveReturnsTheValuesAndVersionWrittenToDisk()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var asset = new ReadingSampleAsset { Name = "saved" };
        using var content = new ReadingSampleBlock { Number = 42, Dependency = new(Guid.NewGuid()) };
        asset.SetBlock(content);
        AssetEntry entry = await asset.SaveAsync(directory.File("one.reading.asset"));
        project.RegisterAsset(entry);
        using var opened = await ReadingSampleAsset.OpenAsync(entry.Path);
        Assert.Equal(asset.Id, opened.Id);
        Assert.Equal("saved", opened.Name);
        Assert.Equal(42, (await opened.GetBlockAsync<ReadingSampleBlock>()).Number);
        Assert.Equal(entry.Version, opened.SourceVersion);
        Assert.Equal(Guid.Empty, asset.SourceVersion);
        Assert.Equal(entry.Dependencies, opened.Describe().Dependencies);
        Assert.Equal(asset.Id, project.Resolve(entry.Path));
    }

    [Fact]
    public async Task CancelledSaveLeavesTheExistingFileAndIndexUntouched()
    {
        using var directory = new AssetTestDirectory();
        string path = directory.File("cancelled.reading.asset");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        using var asset = new ReadingSampleAsset();
        using var content = new ReadingSampleBlock { Number = 1 };
        asset.SetBlock(content);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asset.SaveAsync(path, cancellation.Token).AsTask());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task InvalidOutputSuffixDoesNotReplaceTheExistingFile()
    {
        using var directory = new AssetTestDirectory();
        string path = directory.File("wrong.material.asset");
        byte[] original = [8, 9];
        File.WriteAllBytes(path, original);
        using var asset = new ReadingSampleAsset();
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.SaveAsync(path).AsTask());
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
