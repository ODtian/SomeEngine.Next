namespace SomeEngine.Runtime.Tests;

[CollectionDefinition("Transparency content")]
public sealed class TransparencyContentCollection : ICollectionFixture<TransparencyContentFixture>;

public sealed class TransparencyContentFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Transparency-{Guid.NewGuid():N}");
    public string DirectoryPath => Path.Combine(_directory, "panels");
    public string Spheres => Path.Combine(_directory, "spheres");
    public string Lit => Path.Combine(_directory, "lit");
    public string DeformedPhong => Path.Combine(_directory, "deformed-phong");
    public string DeformedPhongParts => Path.Combine(_directory, "deformed-phong-parts");

    public async Task InitializeAsync()
    {
        string? source = AppContext.BaseDirectory;
        while (source is not null && !File.Exists(Path.Combine(source, "SomeEngine.slnx"))) source = Path.GetDirectoryName(source);
        if (source is null) throw new DirectoryNotFoundException("Engine sources are required to build transparent scene content.");
        try
        {
            string? cookedContent = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_COOKED_CONTENT");
            if (cookedContent is null)
                await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(Spheres, source);
            else
                await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromCookedAsync(Spheres, cookedContent);
            await SomeEngine.SceneContent.BehaviorScenes.CreateIntersectingPanelsAsync(Spheres, DirectoryPath);
            await SomeEngine.SceneContent.BehaviorScenes.CreateLitTransparencyAsync(Spheres, Lit);
            await SomeEngine.SceneContent.BehaviorScenes.CreateDeformedPhongSpheresAsync(Spheres, DeformedPhong);
            await SomeEngine.SceneContent.BehaviorScenes.CreateDeformedPhongPartsAsync(Spheres, DeformedPhongParts);
        }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
