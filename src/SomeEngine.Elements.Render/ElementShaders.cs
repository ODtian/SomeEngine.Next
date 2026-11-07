using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using System.Runtime.CompilerServices;

namespace SomeEngine.Elements.Render;

/// <summary>Holds the immutable built-in shader resources for the application's lifetime.</summary>
internal static class ElementShaders
{
    private static readonly ConditionalWeakTable<IGlobalSession, Dictionary<(string Name, SlangCompileTarget Target), Shader>> s_shaders = new();
    private static readonly object s_gate = new();
    private static IGlobalSession? s_global;
    private static ShaderAssetLibrary? s_assets;

    internal static Shader Get(string name, SlangCompileTarget target, IGlobalSession? global = null)
    {
        lock (s_gate)
        {
            global ??= s_global ??= Shader.CreateSlangGlobalSession();
            var shaders = s_shaders.GetValue(global, static _ => []);
            if (shaders.TryGetValue((name, target), out Shader? shader)) return shader;
            shader = Load(name, target, global);
            shaders.Add((name, target), shader);
            return shader;
        }
    }

    private static Shader Load(string name, SlangCompileTarget target, IGlobalSession global)
    {
        if (s_assets is null)
        {
            using Stream resource = typeof(ElementShaders).Assembly.GetManifestResourceStream(
                "SomeEngine.Elements.Render.Shaders.Elements.assetpack")!;
            using var bytes = new MemoryStream();
            resource.CopyTo(bytes);
            AssetPack pack = AssetPack.OpenAsync(new MemoryRangeSource(bytes.ToArray()), ownsSource: true)
                .AsTask().GetAwaiter().GetResult();
            s_assets = ShaderAssetLibrary.OpenAsync(pack).AsTask().GetAwaiter().GetResult();
        }
        SessionDesc description = ElementShaderSettings.Create(global, target);
        return s_assets.CreateAsync(name, global, description).AsTask().GetAwaiter().GetResult();
    }
}
