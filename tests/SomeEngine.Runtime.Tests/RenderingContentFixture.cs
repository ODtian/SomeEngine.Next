namespace SomeEngine.Runtime.Tests;

[CollectionDefinition("Rendering content")]
public sealed class RenderingContentCollection : ICollectionFixture<RenderingContentFixture>;

public sealed class RenderingContentFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Rendering-{Guid.NewGuid():N}");
    public string Spheres => Path.Combine(_directory, "spheres");
    public string Tessellation => Path.Combine(_directory, "tessellation");
    public string ShaderReplacement => Path.Combine(_directory, "shader");
    public string Streaming => Path.Combine(_directory, "streaming");
    public string Shadows => Path.Combine(_directory, "shadows");
    public string OffCameraShadows => Path.Combine(_directory, "off-camera-shadows");

    public async Task InitializeAsync()
    {
        string? source = AppContext.BaseDirectory;
        while (source is not null && !File.Exists(Path.Combine(source, "SomeEngine.slnx"))) source = Path.GetDirectoryName(source);
        if (source is null) throw new DirectoryNotFoundException("The engine source directory is required to build scene content.");
        try
        {
            await SomeEngine.SceneContent.SceneContent.GenerateSpheresAsync(Spheres, source);
            await SomeEngine.SceneContent.BehaviorScenes.CreateTessellatedSpheresAsync(Spheres, Tessellation, source);
            await SomeEngine.SceneContent.BehaviorScenes.CreateShaderReplacementSpheresAsync(Spheres, ShaderReplacement, source);
            await SomeEngine.SceneContent.BehaviorScenes.CreateStreamingSphereAsync(Spheres, Streaming);
            await SomeEngine.SceneContent.BehaviorScenes.CreateShadowReceiverAsync(Spheres, Shadows);
            await SomeEngine.SceneContent.BehaviorScenes.CreateOffCameraShadowCasterAsync(Shadows, OffCameraShadows);
        }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        Assert.False(Directory.Exists(_directory));
        return Task.CompletedTask;
    }
}
