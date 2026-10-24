using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Transparency.Research;

public static class ResearchShader
{
    private static readonly IGlobalSession Global = CreateSession();

    private static IGlobalSession CreateSession()
    {
        Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global);
        return global;
    }

    public static Shader Compile(string filename, string[] entryNames, bool inspect = false, string definitions = "")
    {
        IGlobalSession global = Global;
        TargetDesc target = new() { Format = SlangCompileTarget.Dxil, Profile = global.FindProfile("sm_6_6") };
        string directory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        global.CreateSession(new SessionDesc
        {
            Targets = [target], SearchPaths = [directory],
            CompilerOptionEntries =
            [
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0)),
                new(CompilerOptionName.Optimization, CompilerOptionValue.FromInt((int)SlangOptimizationLevel.Maximal)),
            ],
        }, out ISession session);
        string path = Path.Combine(directory, filename);
        string source = (inspect ? "#define TRANSPARENCY_INSPECTION 1\n" : "") + definitions + File.ReadAllText(path);
        IModule sourceUnit = session.LoadModuleFromSource(Path.GetFileNameWithoutExtension(filename), path,
            Slang.CreateBlob(source), out ISlangBlob? diagnostics)
            ?? throw new InvalidDataException(diagnostics?.AsString);
        var entries = new IEntryPoint[entryNames.Length];
        for (int index = 0; index < entries.Length; index++)
            sourceUnit.FindEntryPointByName(entryNames[index], out entries[index]);
        session.CreateCompositeComponentType([sourceUnit, .. entries], out IComponentType combined, out diagnostics);
        if (!combined.Link(out IComponentType linked, out diagnostics).Succeeded)
            throw new InvalidDataException(diagnostics?.AsString);
        return Shader.CreateFromSlang(filename, global, session, [sourceUnit], entries, linked, [target]);
    }
}
