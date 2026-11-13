using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private PipelineType ValidateIndirectDescription(Device device, in IndirectCommandLayoutDesc desc)
    {
        bool dxil = device.Capabilities.CompileTarget == SlangCompileTarget.Dxil;
        int actionCount = 0;
        ulong byteCount = 0;
        for (int index = 0; index < desc.Arguments.Length; index++)
        {
            ref readonly IndirectArgumentDesc argument = ref desc.Arguments[index];
            bool action = argument.Type is IndirectArgumentType.Draw or IndirectArgumentType.DrawIndexed or
                IndirectArgumentType.Dispatch or IndirectArgumentType.DispatchMesh or
                IndirectArgumentType.DispatchRays or IndirectArgumentType.WorkGraph;
            if (action)
            {
                actionCount++;
                if (index != desc.Arguments.Length - 1)
                    throw new ArgumentException("The draw or dispatch argument must end an indirect command.", nameof(desc));
            }
            if (argument.Type == IndirectArgumentType.VertexBuffer &&
                (argument.Parameters is not null && argument.Parameters != VariableLayoutReflection.Null ||
                    argument.ByteOffset != 0 || argument.ValueCount != 0))
                throw new ArgumentException("A vertex-buffer argument contains its vertex binding.", nameof(desc));
            if (argument.Type is IndirectArgumentType.Constants or IndirectArgumentType.ConstantBuffer)
                ValidateIndirectParameters(desc.Pipeline, argument);
            byteCount += argument.Type switch
            {
                IndirectArgumentType.Draw or IndirectArgumentType.VertexBuffer or IndirectArgumentType.IndexBuffer => 16,
                IndirectArgumentType.DrawIndexed => 20,
                IndirectArgumentType.Dispatch or IndirectArgumentType.DispatchMesh => 12,
                IndirectArgumentType.DispatchRays => 104,
                IndirectArgumentType.Constants => (ulong)argument.ValueCount * sizeof(uint),
                IndirectArgumentType.ConstantBuffer or IndirectArgumentType.ShaderResource or IndirectArgumentType.UnorderedAccess => 8,
                _ => 0,
            };
        }
        if (actionCount != 1 || !dxil && desc.Arguments.Length != 1)
            throw new ArgumentException("An indirect layout requires one final action and the backend's supported arguments.", nameof(desc));
        if (byteCount > desc.Stride)
            throw new ArgumentOutOfRangeException(nameof(desc), "The stride is smaller than the indirect arguments.");
        return ClassifyIndirectAction(desc.Arguments);
    }

    private void ValidateIndirectParameters(Pipeline? pipeline, in IndirectArgumentDesc argument)
    {
        if (pipeline is null || argument.Parameters is not VariableLayoutReflection layout ||
            layout == VariableLayoutReflection.Null ||
            !_pipelineBindingStates.TryGetValue(pipeline, out PipelineBindingValidationState? bindings) ||
            !bindings.Contains(layout))
            throw new ArgumentException("Indirect root arguments require a parameter layout from the pipeline.", nameof(argument));
        TypeLayoutReflection contents = GetParameterContentsLayout(layout.TypeLayout);
        nuint size = contents.GetSize(SlangParameterCategory.Uniform);
        bool rootConstants = HasParameterCategory(layout.TypeLayout, SlangParameterCategory.PushConstantBuffer) ||
            HasParameterCategory(layout.TypeLayout.ContainerVarLayout, SlangParameterCategory.PushConstantBuffer);
        if (size == 0 || size == Slang.UnknownSize || size == Slang.UnboundedSize)
            throw new ArgumentException("The indirect parameter layout requires ordinary data.", nameof(argument));
        if ((argument.ByteOffset & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(argument));
        if (argument.Type == IndirectArgumentType.Constants)
        {
            ulong bytes = (ulong)argument.ValueCount * sizeof(uint);
            if (!rootConstants || argument.ValueCount == 0 || argument.ByteOffset > size || bytes > size - argument.ByteOffset)
                throw new ArgumentOutOfRangeException(nameof(argument));
        }
        else if (rootConstants || argument.ByteOffset != 0 || argument.ValueCount != 0)
            throw new ArgumentException("An indirect constant-buffer argument targets the root of a constant-buffer layout.", nameof(argument));
    }

    private static bool HasParameterCategory(TypeLayoutReflection layout, SlangParameterCategory category)
    {
        for (uint index = 0; index < layout.CategoryCount; index++)
            if (layout.GetCategoryByIndex(index) == category)
                return true;
        return false;
    }

    private static bool HasParameterCategory(VariableLayoutReflection layout, SlangParameterCategory category)
    {
        for (uint index = 0; index < layout.CategoryCount; index++)
            if (layout.GetCategoryByIndex(index) == category)
                return true;
        return false;
    }
}
