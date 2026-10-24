using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Skinning;

internal static class GpuPoseShaders
{
    private static readonly Dictionary<SlangCompileTarget, Shader> s_shaders = [];
    private static readonly object s_gate = new();
    private static IGlobalSession? s_global;

    internal static Shader Get(SlangCompileTarget target)
    {
        lock (s_gate)
        {
            if (s_shaders.TryGetValue(target, out Shader? shader)) return shader;
            if (s_global is null)
            {
                Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global), null);
                s_global = global;
            }
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
            using Stream source = typeof(GpuPoseShaders).Assembly.GetManifestResourceStream("SomeEngine.Render.Features.Shaders.gpu_pose.slang")!;
            using var reader = new StreamReader(source);
            IModule? module = session.LoadModuleFromSource("gpu_pose", "gpu_pose.slang", Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
            if (module is null) throw new InvalidDataException(diagnostics?.AsString);
            Require(module.FindEntryPointByName("AdvancePoseStates", out IEntryPoint advance), null);
            Require(module.FindEntryPointByName("LoadPoseHistory", out IEntryPoint load), null);
            Require(module.FindEntryPointByName("SamplePoseLevel", out IEntryPoint sample), null);
            Require(module.FindEntryPointByName("StorePoseHistory", out IEntryPoint store), null);
            Require(module.FindEntryPointByName("TranslatePose", out IEntryPoint translate), null);
            Require(module.FindEntryPointByName("SolveTwoBone", out IEntryPoint twoBone), null);
            Require(module.FindEntryPointByName("SolveAim", out IEntryPoint aim), null);
            Require(session.CreateCompositeComponentType([module, advance, load, sample, store, translate, twoBone, aim], out IComponentType combined, out diagnostics), diagnostics);
            Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
            shader = Shader.CreateFromSlang("GPU poses", s_global, session, [module], [advance, load, sample, store, translate, twoBone, aim], linked, [output]);
            s_shaders.Add(target, shader);
            return shader;
        }
    }

    private static void Require(SlangResult result, ISlangBlob? diagnostics)
    {
        if (result.Failed) throw new InvalidDataException($"GPU pose shader compilation failed: {result}. {diagnostics?.AsString}");
    }
}
