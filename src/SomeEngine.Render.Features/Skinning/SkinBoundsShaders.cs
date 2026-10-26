using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Skinning;

internal static class SkinBoundsShaders
{
    private static readonly Dictionary<SlangCompileTarget, Shader> s_shaders = [];
    private static readonly object s_gate = new();
    private static IGlobalSession? s_global;

    internal static Shader Get(SlangCompileTarget target)
    {
        lock (s_gate)
        {
            if (s_shaders.TryGetValue(target, out Shader? shader)) return shader;
            s_global ??= Shader.CreateSlangGlobalSession();
            var output = new TargetDesc
            {
                Format = target,
                Profile = s_global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
                Flags = target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
            };
            Require(s_global.CreateSession(new SessionDesc
            {
                Targets = [output], DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
                CompilerOptionEntries = [new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))],
            }, out ISession session), null);
            using Stream source = typeof(SkinBoundsShaders).Assembly.GetManifestResourceStream("SomeEngine.Render.Features.Shaders.skin_bounds.slang")!;
            using var reader = new StreamReader(source);
            IModule? module = session.LoadModuleFromSource("skin_bounds", "skin_bounds.slang", Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
            if (module is null) throw new InvalidDataException(diagnostics?.AsString);
            Require(module.FindEntryPointByName("TransformSkinBounds", out IEntryPoint entry), null);
            Require(session.CreateCompositeComponentType([module, entry], out IComponentType combined, out diagnostics), diagnostics);
            Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
            shader = Shader.CreateFromSlang("Skin bounds", s_global, session, [module], [entry], linked, [output]);
            s_shaders.Add(target, shader);
            return shader;
        }
    }

    private static void Require(SlangResult result, ISlangBlob? diagnostics)
    {
        if (result.Failed) throw new InvalidDataException($"Skin bounds shader compilation failed: {result}. {diagnostics?.AsString}");
    }
}
