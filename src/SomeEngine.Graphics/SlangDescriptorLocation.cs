using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <summary>The declared address of one resource in a linked Slang descriptor range.</summary>
public readonly record struct SlangDescriptorLocation(SlangParameterCategory Category, ulong Space, ulong Register)
{
    public static SlangDescriptorLocation FromLayout(VariableLayoutReflection parameters,
        TypeLayoutReflection contents, nint set, nint descriptor, nint element = 0)
    {
        SlangParameterCategory category = contents.GetDescriptorSetDescriptorRangeCategory(set, descriptor);
        return new(category,
            checked((ulong)contents.GetDescriptorSetSpaceOffset(set) + (ulong)parameters.GetBindingSpace(category)),
            checked((ulong)contents.GetDescriptorSetDescriptorRangeIndexOffset(set, descriptor) +
                (ulong)parameters.GetOffset(category) + (ulong)element));
    }
}
