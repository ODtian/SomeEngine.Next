using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

internal static class CookedShaderTestProgram
{
    private static readonly Lazy<SlangShaderSharp.ShaderProgram> s_program = new(Load);

    internal static SlangShaderSharp.ShaderProgram Create() => s_program.Value;

    internal static void Load(Shader value)
    {
        using Shader shader = Shader.Create(value.Name ?? "test", value.Programs!.OfType<SlangShaderSharp.ShaderProgram>(),
            value.EntryPointReflections ?? [], value.Metadata);
    }

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
}
