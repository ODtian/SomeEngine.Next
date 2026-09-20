using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

internal static class CookedShaderTestProgram
{
    private static readonly Lazy<SlangShaderSharp.ShaderProgram> s_program = new(Load);

    internal static SlangShaderSharp.ShaderProgram Create() => s_program.Value;

    private static SlangShaderSharp.ShaderProgram Load()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "hello_triangle.shader.asset");
        using ShaderAsset asset = ShaderAsset.OpenAsync(path).AsTask().GetAwaiter().GetResult();
        using Shader shader = asset.GetBlock<ShaderBlock>().CreateAsync(asset.Name).AsTask().GetAwaiter().GetResult();
        return shader.Programs!.Single(program =>
            string.Equals(program!.Backend, "spirv", StringComparison.Ordinal))!;
    }
    internal static Shader Load(Shader value)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-shader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new AssetProject(directory, []);
            AssetGuid guid = project.CreateAsset("test.shader.asset", value);
            var loader = new AssetLoader(project.CreateStorage());
            try { return loader.LoadAsync(new AssetId<Shader>(guid)).AsTask().GetAwaiter().GetResult(); }
            finally { loader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
