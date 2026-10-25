using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Runtime.Tests;

[CollectionDefinition("Scene content")]
public sealed class SceneContentCollection : ICollectionFixture<SceneContentFixture>;

public sealed class SceneContentFixture : IAsyncLifetime
{
    private readonly string? _existingScene = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_EXISTING_SCENE");
    public string DirectoryPath { get; private set; } = Path.Combine(Path.GetTempPath(), $"SomeEngine-Scenes-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        if (_existingScene is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(_existingScene, "SOMEENGINE_TEST_EXISTING_SCENE");
            string existingDirectory = Path.GetFullPath(_existingScene);
            if (!Directory.Exists(existingDirectory) || !AssetContent.Exists(existingDirectory))
                throw new DirectoryNotFoundException($"The existing test scene has no asset catalog or pack list: {existingDirectory}");
            await using AssetContent content = await AssetContent.OpenAsync(existingDirectory);
            IReadOnlyList<AssetEntry> applications = content.Manifest.List(ApplicationSettingsAsset.FileType);
            if (applications.Count != 1)
                throw new InvalidDataException($"The existing test scene must contain exactly one application asset; found {applications.Count}.");
            await using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(content.Loader,
                new AssetId<ApplicationSettingsAsset>(applications[0].AssetGuid));
            ApplicationSettingsBlock boot = await settings.GetBlockAsync<ApplicationSettingsBlock>();
            await using WorldAsset scene = await WorldAsset.OpenAsync(content.Loader, boot.Scene);
            Game.Assets.MaterialTypes.Register();
            DirectoryPath = existingDirectory;
            Console.WriteLine($"Scene fixture: reusing existing scene without generating content: {DirectoryPath}");
            return;
        }
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SomeEngine.slnx")))
            directory = Path.GetDirectoryName(directory);
        if (directory is null) throw new DirectoryNotFoundException("The engine source directory is required to generate scene content.");
        try { await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(DirectoryPath, directory); }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        if (_existingScene is not null) return Task.CompletedTask;
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        Assert.False(Directory.Exists(DirectoryPath));
        return Task.CompletedTask;
    }
}
