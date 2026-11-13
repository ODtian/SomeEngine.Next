using SomeEngine.Assets;

namespace SomeEngine.Runtime;

/// <summary>File access and manifest data for one content directory.</summary>
internal sealed class ApplicationContent(string directory, AssetLoader files, AssetManifest manifest)
{
    internal string Directory { get; } = directory;
    internal AssetLoader Files { get; } = files;
    internal AssetManifest Manifest { get; } = manifest;
}
