namespace SomeEngine.Runtime.Tests;

[CollectionDefinition("Scene content")]
public sealed class SceneContentCollection : ICollectionFixture<SceneContentFixture>;

public sealed class SceneContentFixture : IAsyncLifetime
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"SomeEngine-Scenes-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SomeEngine.slnx")))
            directory = Path.GetDirectoryName(directory);
        if (directory is null) throw new DirectoryNotFoundException("The engine source directory is required to generate scene content.");
        try { await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(DirectoryPath, directory); }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        Assert.False(Directory.Exists(DirectoryPath));
        return Task.CompletedTask;
    }
}
