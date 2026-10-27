using System.Text;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Animation.Pose;

/// <summary>The shared sampling, rig and typed body-chain compute entries for a selected device.</summary>
public static class DevicePoseShaders
{
    private static readonly Dictionary<SlangCompileTarget, PoseShader> s_shaders = [];
    private static readonly object s_gate = new();
    private static IGlobalSession? s_global;

    public static DevicePoseKernels Get(SlangCompileTarget target) => GetShader(target).Kernels;
    public static DevicePoseBodyKernels GetBodies(SlangCompileTarget target) => GetShader(target).Bodies;

    private static PoseShader GetShader(SlangCompileTarget target)
    {
        lock (s_gate)
        {
            if (s_shaders.TryGetValue(target, out PoseShader ready)) return ready;
            s_global ??= Shader.CreateSlangGlobalSession();
            var output = new TargetDesc
            {
                Format = target,
                Profile = s_global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
                Flags = target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
            };
            Check(s_global.CreateSession(new SessionDesc
            {
                Targets = [output], DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
                CompilerOptionEntries = [new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))],
            }, out ISession session));
            IModule? unit = session.LoadModuleFromSource("game_pose", "gpu_pose_physics.slang",
                Slang.CreateBlob(ReadSource("gpu_pose_physics.slang")), out ISlangBlob? diagnostics);
            if (unit is null) throw new InvalidDataException(diagnostics?.AsString);
            string[] names = ["AdvancePoseStates", "SamplePoseLevel", "TranslatePose", "SolveTwoBone", "SolveAim", "WarpStride", "SimulateDevicePoseChain",
                "WriteDevicePoseBodyTargets", "MapDevicePoseBodyBones"];
            var entries = new IEntryPoint[names.Length];
            var components = new IComponentType[names.Length + 1];
            components[0] = unit;
            for (int index = 0; index < entries.Length; index++)
            {
                Check(unit.FindEntryPointByName(names[index], out entries[index]));
                components[index + 1] = entries[index];
            }
            Check(session.CreateCompositeComponentType(components, out IComponentType combined, out diagnostics), diagnostics);
            Check(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
            Shader shader = Shader.CreateFromSlang("Game pose sources", s_global, session, [unit], entries, linked, [output], generateLibraryCode: false);
            ComputePipelineDesc Entry(int index)
            {
                EntryPointReflection entry = shader.RequireSlangEntry(0, names[index], SlangStage.Compute);
                return new(shader.SlangCode, shader.GetSlangReflection(0), entry, shader.GetSlangEntryBytes(0, entry), names[index],
                    CodeBlob: shader.GetSlangEntryBlob(0, entry));
            }
            var kernels = new DevicePoseKernels(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
            var compiled = new PoseShader(shader, kernels, new(Entry(7), Entry(8)));
            s_shaders.Add(target, compiled);
            return compiled;
        }
    }

    private static string ReadSource(string name)
    {
        using Stream source = typeof(DevicePoseShaders).Assembly.GetManifestResourceStream("SomeEngine.Animation.Pose.Shaders." + name)!;
        using var reader = new StreamReader(source);
        var code = new StringBuilder();
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("#include \"", StringComparison.Ordinal)) code.AppendLine(ReadSource(line[10..^1]));
            else code.AppendLine(line);
        return code.ToString();
    }

    private static void Check(SlangResult result, ISlangBlob? diagnostics = null)
    { if (result.Failed) throw new InvalidDataException($"Game pose shader compilation failed: {result}. {diagnostics?.AsString}"); }
    private readonly record struct PoseShader(Shader Shader, DevicePoseKernels Kernels, DevicePoseBodyKernels Bodies);
}
