using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <summary>The declared address of one resource in a linked Slang descriptor range.</summary>
public readonly record struct SlangDescriptorLocation(SlangParameterCategory Category, ulong Space, ulong Register)
{
    public static SlangDescriptorLocation FromLayout(VariableLayoutReflection parameters,
        TypeLayoutReflection contents, nint set, nint descriptor, nint element = 0)
    {
        SlangParameterCategory category = contents.GetDescriptorSetDescriptorRangeCategory(set, descriptor);
        ulong space = checked((ulong)contents.GetDescriptorSetSpaceOffset(set) +
            (ulong)parameters.GetBindingSpace(category));
        ulong register = checked((ulong)contents.GetDescriptorSetDescriptorRangeIndexOffset(set, descriptor) +
            (ulong)parameters.GetOffset(category));
        if ((contents.GetDescriptorSetDescriptorRangeType(set, descriptor) & SlangBindingType.BaseMask)
            == SlangBindingType.ConstantBuffer)
        {
            for (nint index = 0; index < contents.SubObjectRangeCount; index++)
            {
                nint range = contents.GetSubObjectRangeBindingRangeIndex(index);
                nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
                nint count = contents.GetBindingRangeDescriptorRangeCount(range);
                if (contents.GetBindingRangeDescriptorSetIndex(range) != set ||
                    descriptor < first || descriptor - first >= count) continue;
                VariableLayoutReflection child = contents.GetSubObjectRangeOffset(index);
                // A constant-buffer descriptor carries its declared space on the child.
                // Its register is independent of the enclosing ordinary-data buffer.
                space = checked(space + (ulong)child.GetBindingSpace(category));
                register = (ulong)child.GetOffset(category);
                break;
            }
        }
        return new(category, space, checked(register + (ulong)element));
    }
}
