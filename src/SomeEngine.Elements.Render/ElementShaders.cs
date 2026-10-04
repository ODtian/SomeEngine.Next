using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Elements.Render;

/// <summary>Holds the immutable built-in shader resources for the application's lifetime.</summary>
internal static class ElementShaders
{
    private static readonly Dictionary<(string Name, SlangCompileTarget Target), Shader> s_shaders = [];
    private static readonly object s_gate = new();
    private static IGlobalSession? s_global;

    internal static Shader Get(string name, SlangCompileTarget target)
    {
        lock (s_gate)
        {
            if (s_shaders.TryGetValue((name, target), out Shader? shader)) return shader;
            shader = Compile(name, target);
            s_shaders.Add((name, target), shader);
            return shader;
        }
    }

    private static Shader Compile(string name, SlangCompileTarget target)
    {
        if (s_global is null)
        {
            Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global), null);
            s_global = global;
        }
        var targetDescription = new TargetDesc
        {
            Format = target,
            Profile = s_global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
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
        Require(s_global.CreateSession(description, out ISession session), null);
        using Stream source = typeof(ElementShaders).Assembly.GetManifestResourceStream(
            $"SomeEngine.Elements.Render.Shaders.{name}.slang")!;
        using var reader = new StreamReader(source);
        IModule? shaderSource = session.LoadModuleFromSource(name, $"{name}.slang",
            Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
        if (shaderSource is null) throw new InvalidDataException(DiagnosticText(diagnostics));
        Require(shaderSource.FindEntryPointByName("vertexMain", out IEntryPoint vertex), null);
        Require(shaderSource.FindEntryPointByName("pixelMain", out IEntryPoint pixel), null);
        Require(session.CreateCompositeComponentType([shaderSource, vertex, pixel],
            out IComponentType combined, out diagnostics), diagnostics);
        Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
        return Shader.CreateFromSlang(name, s_global, session, [shaderSource], [vertex, pixel], linked, [targetDescription]);
    }

    private static void Require(SlangResult result, ISlangBlob? diagnostics)
    {
        if (result.Failed) throw new InvalidDataException($"Element shader compilation failed: {result}. {DiagnosticText(diagnostics)}");
    }

    private static string DiagnosticText(ISlangBlob? diagnostics) => diagnostics is null ? string.Empty : diagnostics.AsString;
}
