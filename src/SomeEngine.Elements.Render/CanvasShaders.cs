using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Elements.Render;

/// <summary>Shared immutable built-in shaders and their Slang data for the life of the process.</summary>
internal static class CanvasShaders
{
    private static readonly object s_shaderLock = new();
    private static readonly Dictionary<(string Name, SlangCompileTarget Target), Shader> s_shaders = [];
    private static IGlobalSession? s_global;

    internal static Shader Get(string name, SlangCompileTarget target)
    {
        lock (s_shaderLock)
        {
            if (s_shaders.TryGetValue((name, target), out Shader? shader)) return shader;
            if (s_global is null)
            {
                Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global), null);
                s_global = global;
            }
            shader = Compile(s_global, name, target);
            s_shaders.Add((name, target), shader);
            return shader;
        }
    }

    private static Shader Compile(IGlobalSession global, string name, SlangCompileTarget target)
    {
        var targetDescription = new TargetDesc
        {
            Format = target,
            Profile = global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
            Flags = target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
        };
        var description = new SessionDesc
        {
            Targets = [targetDescription],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0)),
            ],
        };
        Require(global.CreateSession(description, out ISession session), null);
        using Stream source = typeof(CanvasShaders).Assembly.GetManifestResourceStream(
            $"SomeEngine.Elements.Render.Shaders.{name}.slang")!;
        using var reader = new StreamReader(source);
        IModule? sourceCode = session.LoadModuleFromSource(name, $"{name}.slang",
            Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
        if (sourceCode is null) throw new InvalidDataException(DiagnosticText(diagnostics));
        Require(sourceCode.FindEntryPointByName("vertexMain", out IEntryPoint vertex), null);
        Require(sourceCode.FindEntryPointByName("pixelMain", out IEntryPoint pixel), null);
        Require(session.CreateCompositeComponentType([sourceCode, vertex, pixel],
            out IComponentType combined, out diagnostics), diagnostics);
        Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
        return Shader.CreateFromSlang(name, global, session, [sourceCode], [vertex, pixel], linked, [targetDescription]);
    }

    private static void Require(SlangResult result, ISlangBlob? diagnostics)
    {
        if (result.Failed) throw new InvalidDataException($"Canvas shader compilation failed: {result}. {DiagnosticText(diagnostics)}");
    }

    private static string DiagnosticText(ISlangBlob? diagnostics) => diagnostics is null ? string.Empty : diagnostics.AsString;
}
