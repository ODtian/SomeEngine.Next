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
        try
        {
            string? cookedContent = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_COOKED_CONTENT");
            if (cookedContent is null)
                await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(DirectoryPath, directory);
            else
            {
                Console.WriteLine($"Scene fixture: reusing explicit cooked package {Path.GetFullPath(cookedContent)}; generating only sphere materials and scene.");
                await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromCookedAsync(DirectoryPath, cookedContent);
            }
        }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        Assert.False(Directory.Exists(DirectoryPath));
        return Task.CompletedTask;
    }
}
