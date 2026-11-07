using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using System.Runtime.CompilerServices;

namespace SomeEngine.Render.Features.Skinning;

internal static class SkinningShaders
{
    private static readonly object s_gate = new();
    private static readonly ConditionalWeakTable<IGlobalSession, Dictionary<(string Name, SlangCompileTarget Target), Shader>> s_shaders = new();
    private static IGlobalSession? s_global;
    private static ShaderAssetLibrary? s_assets;

    internal static Shader Get(string name, SlangCompileTarget target, IGlobalSession? global)
    {
        lock (s_gate)
        {
            global ??= s_global ??= Shader.CreateSlangGlobalSession();
            var shaders = s_shaders.GetValue(global, static _ => []);
            if (shaders.TryGetValue((name, target), out Shader? shader)) return shader;
            if (s_assets is null)
            {
                using Stream source = typeof(SkinningShaders).Assembly.GetManifestResourceStream(
                    "SomeEngine.Render.Features.Shaders.Skinning.assetpack")!;
                using var bytes = new MemoryStream();
                source.CopyTo(bytes);
                AssetPack pack = AssetPack.OpenAsync(new MemoryRangeSource(bytes.ToArray()), ownsSource: true)
                    .AsTask().GetAwaiter().GetResult();
                s_assets = ShaderAssetLibrary.OpenAsync(pack).AsTask().GetAwaiter().GetResult();
            }
            shader = s_assets.CreateAsync(name, global, SkinningShaderSettings.Create(global, target))
                .AsTask().GetAwaiter().GetResult();
            shaders.Add((name, target), shader);
            return shader;
        }
    }
}
