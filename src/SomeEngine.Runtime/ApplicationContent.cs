using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

/// <summary>Application file data and the actual shaders shared by its scenes.</summary>
internal sealed class ApplicationContent(
    string directory, RuntimeGraphicsBackend graphicsBackend, DateTime catalogWriteTime,
    AssetLoader files, AssetManifest manifest, IGlobalSession slang, SessionDesc shaderSettings)
{
    internal string Directory { get; } = directory;
    internal RuntimeGraphicsBackend GraphicsBackend { get; } = graphicsBackend;
    internal DateTime CatalogWriteTime { get; } = catalogWriteTime;
    internal AssetLoader Files { get; } = files;
    internal AssetManifest Manifest { get; } = manifest;
    internal IGlobalSession Slang { get; } = slang;
    internal SessionDesc ShaderSettings { get; } = shaderSettings;
    internal WeakValues<AssetResourceKey<ShaderAsset>, Shader> ShaderValues { get; } = new();
    internal WeakValues<AssetResourceKey<TextureAsset>, Texture> TextureValues { get; } = new();
    internal ClusterShaders? Pipeline { get; set; }
    internal VirtualShadowMapShaders? Shadows { get; set; }
    internal Shader[] SharedShaders { get; set; } = [];
}
