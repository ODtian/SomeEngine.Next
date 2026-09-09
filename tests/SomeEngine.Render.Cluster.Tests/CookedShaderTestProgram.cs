using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Cluster.Tests;

internal static class CookedShaderTestProgram
{
    private static readonly Lazy<CookedShaderProgram> s_program = new(Load);

    internal static CookedShaderProgram Create()
    {
        CookedShaderProgram source = s_program.Value;
        return new CookedShaderProgram
        {
            Backend = source.Backend,
            Data = source.Data!.Value.ToArray(),
            ContentHash = source.ContentHash,
            Program = source.Program,
        };
    }

    private static CookedShaderProgram Load()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "hello_triangle.shader.asset");
        ShaderData shader = ShaderData.ReadAsync(path).AsTask().GetAwaiter().GetResult();
        return shader.Programs!.Single(program =>
            string.Equals(program.Backend, "spirv", StringComparison.Ordinal));
    }
}
