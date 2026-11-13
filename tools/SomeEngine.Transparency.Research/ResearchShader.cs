using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Transparency.Research;

public static class ResearchShader
{
    private static readonly ShaderLibrary Library = CreateLibrary();

    private static ShaderLibrary CreateLibrary()
    {
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        return new ShaderLibrary(global, new SessionDesc
        {
            Targets = [new() { Format = SlangCompileTarget.Dxil, Profile = global.FindProfile("sm_6_6") }],
            SearchPaths = [Path.Combine(AppContext.BaseDirectory, "Shaders")],
            CompilerOptionEntries =
            [
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0)),
                new(CompilerOptionName.Optimization, CompilerOptionValue.FromInt((int)SlangOptimizationLevel.Maximal)),
            ],
        });
    }

    public static Shader Compile(string filename, string[] entryNames, bool inspect = false, string definitions = "")
    {
        string source = (inspect ? "#define TRANSPARENCY_INSPECTION 1\n" : "") + definitions +
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", filename));
        return Library.CreateAsync(filename, filename, source).AsTask().GetAwaiter().GetResult();
    }
}
