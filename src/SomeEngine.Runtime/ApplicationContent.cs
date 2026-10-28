using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

/// <summary>Application file data and the actual shaders shared by its scenes.</summary>
internal sealed class ApplicationContent(
    string directory, RuntimeGraphicsBackend graphicsBackend,
    AssetContent assets, IGlobalSession slang, SessionDesc shaderSettings)
{
    private ClusterShaders? _clusterShaders;

    internal string Directory { get; } = directory;
    internal RuntimeGraphicsBackend GraphicsBackend { get; } = graphicsBackend;
    internal AssetContent Assets { get; } = assets;
    internal AssetLoader Files => Assets.Loader;
    internal AssetManifest Manifest => Assets.Manifest;
    internal IGlobalSession Slang { get; } = slang;
    internal SessionDesc ShaderSettings { get; } = shaderSettings;
    internal WeakValues<AssetResourceKey<ShaderAsset>, Shader> ShaderValues { get; } = new();
    internal WeakValues<AssetResourceKey<TextureAsset>, Texture> TextureValues { get; } = new();
    internal VirtualShadowMapShaders? Shadows { get; set; }
    internal Shader[] SharedShaders { get; set; } = [];

    internal ClusterShaders ReuseClusterShaders(ClusterShaders shaders)
    {
        if (_clusterShaders is { } current && current.Name == shaders.Name &&
            current.Operations.Count == shaders.Operations.Count &&
            current.Operations.Zip(shaders.Operations).All(pair =>
                pair.First.Role == pair.Second.Role &&
                pair.First.Shaders.SequenceEqual(pair.Second.Shaders, ReferenceEqualityComparer.Instance) &&
                pair.First.Entries.SequenceEqual(pair.Second.Entries)))
            return current;
        return _clusterShaders = shaders;
    }
}
