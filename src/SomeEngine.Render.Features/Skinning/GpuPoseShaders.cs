using SlangShaderSharp;
using SomeEngine.Graphics;
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
            using Stream source = typeof(GpuPoseShaders).Assembly.GetManifestResourceStream("SomeEngine.Render.Features.Shaders.gpu_pose.slang")!;
            using var reader = new StreamReader(source);
            IModule? module = session.LoadModuleFromSource("gpu_pose", "gpu_pose.slang", Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
            if (module is null) throw new InvalidDataException(diagnostics?.AsString);
            SlangShaderData.RetainSession(session, module);
            Require(module.FindEntryPointByName("AdvancePoseStates", out IEntryPoint advance), null);
            SlangShaderData.RetainSession(session, advance);
            Require(module.FindEntryPointByName("LoadPoseHistory", out IEntryPoint load), null);
            SlangShaderData.RetainSession(session, load);
            Require(module.FindEntryPointByName("SamplePoseLevel", out IEntryPoint sample), null);
            SlangShaderData.RetainSession(session, sample);
            Require(module.FindEntryPointByName("StorePoseHistory", out IEntryPoint store), null);
            SlangShaderData.RetainSession(session, store);
            Require(module.FindEntryPointByName("TranslatePose", out IEntryPoint translate), null);
            SlangShaderData.RetainSession(session, translate);
            Require(module.FindEntryPointByName("SolveTwoBone", out IEntryPoint twoBone), null);
            SlangShaderData.RetainSession(session, twoBone);
            Require(module.FindEntryPointByName("SolveAim", out IEntryPoint aim), null);
            SlangShaderData.RetainSession(session, aim);
            Require(session.CreateCompositeComponentType([module, advance, load, sample, store, translate, twoBone, aim], out IComponentType combined, out diagnostics), diagnostics);
            SlangShaderData.RetainSession(session, combined);
            Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
            SlangShaderData.RetainSession(session, linked);
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
