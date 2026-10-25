using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Liquid;

internal static class LiquidShader
{
    private static readonly Dictionary<string, Shader> _shaders = [];
    private static readonly IGlobalSession _compiler = CreateCompiler();

    private static IGlobalSession CreateCompiler()
    {
        Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession compiler);
        return compiler;
    }

    public static Shader Compile(params string[] entryNames)
    {
        string name = string.Join("_", entryNames);
        if (_shaders.TryGetValue(name, out Shader? shader)) return shader;
        IGlobalSession compiler = _compiler;
        TargetDesc target = new() { Format = SlangCompileTarget.Dxil, Profile = compiler.FindProfile("sm_6_6") };
        compiler.CreateSession(new SessionDesc
        {
            Targets = [target], SearchPaths = [Path.Combine(AppContext.BaseDirectory, "Shaders")],
            CompilerOptionEntries =
            [new(CompilerOptionName.Optimization, CompilerOptionValue.FromInt((int)SlangOptimizationLevel.Maximal))],
        }, out ISession session);
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "liquid.slang");
        string definitions = $"#define LIQUID_CAPACITY {LiquidFlow.Capacity}\n#define LIQUID_CURVES {LiquidRendering.CurveCount}\n#define LIQUID_ALONG {LiquidRendering.AxialSegments}\n#define LIQUID_SIDES {LiquidRendering.RadialSegments}\n" +
            string.Concat(Enum.GetValues<LiquidContactSurface>().Select(surface => $"#define CONTACT_{surface} {(int)surface}\n")) +
            string.Concat(entryNames.Select(entry => "#define STAGE_" + entry + " 1\n"));
        var source = session.LoadModuleFromSource("liquid", path, Slang.CreateBlob(definitions + File.ReadAllText(path)), out ISlangBlob? diagnostics)
            ?? throw new InvalidDataException(diagnostics?.AsString);
        IEntryPoint[] entries = entryNames.Select(entryName =>
        { source.FindEntryPointByName(entryName, out IEntryPoint entry); return entry; }).ToArray();
        session.CreateCompositeComponentType([source, .. entries], out IComponentType combined, out diagnostics);
        if (!combined.Link(out IComponentType linked, out diagnostics).Succeeded)
            throw new InvalidDataException(diagnostics?.AsString);
        shader = Shader.CreateFromSlang(name, compiler, session, [source], entries, linked, [target]);
        _shaders.Add(name, shader);
        return shader;
    }
}
