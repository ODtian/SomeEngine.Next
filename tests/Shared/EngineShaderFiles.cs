using SomeEngine.Assets;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

internal sealed class EngineShaderFiles : IDisposable
{
    internal AssetLoader Files { get; }

    internal EngineShaderFiles()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) directory = directory.Parent!;
        string root = directory.FullName;
        Files = EngineShaderContent.OpenFiles(root, AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest")));
    }

    public void Dispose() => Files.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
