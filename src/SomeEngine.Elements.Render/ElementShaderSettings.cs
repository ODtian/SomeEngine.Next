using SlangShaderSharp;

namespace SomeEngine.Elements.Render;

internal static class ElementShaderSettings
{
    internal static SessionDesc Create(IGlobalSession global, params SlangCompileTarget[] targets) => new()
    {
        Targets = targets.Select(target => new TargetDesc
        {
            Format = target,
            Profile = global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
            Flags = target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
        }).ToArray(),
        DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
        CompilerOptionEntries =
        [new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
         new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))],
    };
}
