namespace SomeEngine.Runtime.Tests;

[CollectionDefinition("Rendering content")]
public sealed class RenderingContentCollection : ICollectionFixture<RenderingContentFixture>;

public sealed class RenderingContentFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Rendering-{Guid.NewGuid():N}");
    private readonly Lazy<Task<string>> _tessellation;
    private readonly Lazy<Task<string>> _shaderReplacement;
    private readonly Lazy<Task<string>> _streaming;
    private readonly Lazy<Task<string>> _shadows;
    private readonly Lazy<Task<string>> _unboundedHistory;
    private string _sourceDirectory = null!;
    public string Spheres => Path.Combine(_directory, "spheres");
    public Task<string> Tessellation => _tessellation.Value;
    public Task<string> ShaderReplacement => _shaderReplacement.Value;
    public Task<string> Streaming => _streaming.Value;
    public Task<string> Shadows => _shadows.Value;
    public Task<string> UnboundedHistory => _unboundedHistory.Value;

    public RenderingContentFixture()
    {
        _tessellation = new(CreateTessellationAsync);
        _shaderReplacement = new(CreateShaderReplacementAsync);
        _streaming = new(CreateStreamingAsync);
        _shadows = new(CreateShadowsAsync);
        _unboundedHistory = new(CreateUnboundedHistoryAsync);
    }

    public async Task InitializeAsync()
    {
        string? source = AppContext.BaseDirectory;
        while (source is not null && !File.Exists(Path.Combine(source, "SomeEngine.slnx"))) source = Path.GetDirectoryName(source);
        if (source is null) throw new DirectoryNotFoundException("The engine source directory is required to build scene content.");
        _sourceDirectory = source;
        try
        {
            await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromSharedAsync(Spheres,
                Path.Combine(source, "samples", "DefaultGame"), source);
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task<string> CreateTessellationAsync()
    {
        string path = Path.Combine(_directory, "tessellation");
        await SomeEngine.SceneContent.BehaviorScenes.CreateTessellatedSpheresAsync(Spheres, path, _sourceDirectory);
        return path;
    }

    private async Task<string> CreateShaderReplacementAsync()
    {
        string path = Path.Combine(_directory, "shader");
        await SomeEngine.SceneContent.BehaviorScenes.CreateShaderReplacementSpheresAsync(Spheres, path, _sourceDirectory);
        return path;
    }

    private async Task<string> CreateStreamingAsync()
    {
        string path = Path.Combine(_directory, "streaming");
        await SomeEngine.SceneContent.BehaviorScenes.CreateStreamingSphereAsync(Spheres, path);
        return path;
    }

    private async Task<string> CreateShadowsAsync()
    {
        string path = Path.Combine(_directory, "shadows");
        await SomeEngine.SceneContent.BehaviorScenes.CreateShadowReceiverAsync(Spheres, path);
        return path;
    }

    private async Task<string> CreateUnboundedHistoryAsync()
    {
        string path = Path.Combine(_directory,"unbounded-history");
        await SomeEngine.SceneContent.BehaviorScenes.CreateUnboundedHistoryDefaultAsync(
            Path.Combine(_sourceDirectory,"samples","DefaultGame"),path,_sourceDirectory);
        return path;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        Assert.False(Directory.Exists(_directory));
        return Task.CompletedTask;
    }
}
