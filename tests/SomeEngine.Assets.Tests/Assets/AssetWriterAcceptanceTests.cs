namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetWriterAcceptanceTests
{
    [Fact]
    public async Task SaveReturnsTheValuesAndVersionWrittenToDisk()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var asset = new ReadingSample.Asset { Name = "saved", Number = 42, Dependency = Guid.NewGuid() };
        AssetEntry entry = await asset.SaveAsync(directory.File("one.reading.asset"));
        project.RegisterAsset(entry);
        using var opened = await ReadingSample.Asset.OpenAsync(entry.Path);
        Assert.Equal(asset.Id, opened.Id);
        Assert.Equal("saved", opened.Name);
        Assert.Equal(42, opened.Number);
        Assert.Equal(entry.Version, opened.SourceVersion);
        Assert.Equal(Guid.Empty, asset.SourceVersion);
        Assert.Equal(entry.Dependencies, opened.Describe().Dependencies);
        Assert.Equal(asset.Id.Value, project.Resolve(entry.Path));
    }

    [Fact]
    public async Task CancelledSaveLeavesTheExistingFileAndIndexUntouched()
    {
        using var directory = new AssetTestDirectory();
        string path = directory.File("cancelled.reading.asset");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        using var asset = new ReadingSample.Asset { Number = 1 };
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
        using var asset = new ReadingSample.Asset();
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.SaveAsync(path).AsTask());
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
