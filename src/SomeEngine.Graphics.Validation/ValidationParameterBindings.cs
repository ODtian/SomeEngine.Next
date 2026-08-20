using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static TypeLayoutReflection GetParameterContentsLayout(TypeLayoutReflection layout)
    {
        TypeLayoutReflection contents = layout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            TypeLayoutReflection element = contents.ElementTypeLayout.UnwrapArray();
            if (element != TypeLayoutReflection.Null)
                contents = element;
        }
        return contents;
    }

    private static string? DiagnoseParameterBindings(
        VariableLayoutReflection layout,
        ReadOnlySpan<ResourceBinding> bindings,
        ReadOnlySpan<byte> ordinaryData,
        PipelineBindingValidationState? pipeline = null)
    {
        TypeLayoutReflection contents = GetParameterContentsLayout(layout.TypeLayout);
        nuint ordinarySize = contents.GetSize(SlangParameterCategory.Uniform);
        if (ordinarySize == Slang.UnknownSize || ordinarySize == Slang.UnboundedSize ||
            ordinarySize > int.MaxValue)
        {
            throw new GraphicsException(GraphicsError.NativeFailure,
                $"Slang parameter layout '{layout.Name}' has an unresolved ordinary-data size.");
        }
        if (ordinaryData.Length != checked((int)ordinarySize))
            return $"The Slang parameter layout requires exactly {ordinarySize} ordinary-data bytes; the supplied packet has {ordinaryData.Length} bytes.";

        nint reflectedBindingRangeCount = contents.BindingRangeCount;
        nint reflectedDescriptorSetCount = contents.DescriptorSetCount;
        nint bindingRangeCount = GetResolvedSlangCount(
            reflectedBindingRangeCount,
            $"Slang layout '{layout.Name}' has unresolved binding-range or descriptor-set counts.");
        nint descriptorSetCount = GetResolvedSlangCount(
            reflectedDescriptorSetCount,
            $"Slang layout '{layout.Name}' has unresolved binding-range or descriptor-set counts.");

        int ordinal = 0;
        for (nint rangeIndex = 0; rangeIndex < bindingRangeCount; rangeIndex++)
        {
            string? diagnostic = DiagnoseBindingRange(
                contents,
                layout,
                rangeIndex,
                descriptorSetCount,
                bindings,
                pipeline,
                ref ordinal);
            if (diagnostic is not null)
                return diagnostic;
        }
        return ordinal == bindings.Length ? null :
            $"The Slang parameter layout requires exactly {ordinal} bounded resource bindings; the supplied packet has {bindings.Length} bindings.";
    }

    private static nint GetResolvedSlangCount(nint count, string diagnostic)
    {
        nuint marker = unchecked((nuint)count);
        if (count < 0 || marker == Slang.UnknownSize || marker == Slang.UnboundedSize)
            throw new GraphicsException(GraphicsError.NativeFailure, diagnostic);
        return count;
    }

    private static string? DiagnoseBindingRange(
        TypeLayoutReflection contents,
        VariableLayoutReflection layout,
        nint rangeIndex,
        nint descriptorSetCount,
        ReadOnlySpan<ResourceBinding> bindings,
        PipelineBindingValidationState? pipeline,
        ref int ordinal)
    {
        nint descriptorRangeCount = GetResolvedSlangCount(
            contents.GetBindingRangeDescriptorRangeCount(rangeIndex),
            $"Slang binding range {rangeIndex} on layout '{layout.Name}' has an unresolved descriptor-range count.");
        if (descriptorRangeCount == 0)
            return null;

        nint reflectedCount = contents.GetBindingRangeBindingCount(rangeIndex);
        nuint reflectedCountMarker = unchecked((nuint)reflectedCount);
        bool logicalUnbounded = reflectedCountMarker == Slang.UnboundedSize;
        if (!logicalUnbounded &&
            (reflectedCountMarker == Slang.UnknownSize || reflectedCount <= 0 ||
             reflectedCountMarker > uint.MaxValue))
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang binding range {rangeIndex} has an invalid descriptor count {reflectedCount}.");
        }

        (nint setIndex, nint firstDescriptorRangeIndex) = ValidateDescriptorRangeLocation(
            contents,
            layout,
            rangeIndex,
            descriptorRangeCount,
            descriptorSetCount);
        string fieldName = contents.GetBindingRangeLeafVariable(rangeIndex).Name;
        if (string.IsNullOrEmpty(fieldName))
            fieldName = $"binding range {rangeIndex}";

        for (nint relativeDescriptorRange = 0;
             relativeDescriptorRange < descriptorRangeCount;
             relativeDescriptorRange++)
        {
            nint descriptorRangeIndex = firstDescriptorRangeIndex + relativeDescriptorRange;
            nint nativeCount = contents.GetDescriptorSetDescriptorRangeDescriptorCount(
                setIndex,
                descriptorRangeIndex);
            nuint nativeCountMarker = unchecked((nuint)nativeCount);
            bool nativeUnbounded = nativeCountMarker == Slang.UnboundedSize;
            if ((!nativeUnbounded &&
                 (nativeCount <= 0 || nativeCountMarker == Slang.UnknownSize ||
                  nativeCountMarker > uint.MaxValue)) ||
                nativeUnbounded != logicalUnbounded)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang descriptor range {descriptorRangeIndex} for binding range " +
                    $"{rangeIndex} on layout '{layout.Name}' has invalid descriptor " +
                    $"count {nativeCount}.");
            }

            SlangBindingType reflected = contents.GetDescriptorSetDescriptorRangeType(
                setIndex,
                descriptorRangeIndex);
            SlangParameterCategory category =
                contents.GetDescriptorSetDescriptorRangeCategory(setIndex, descriptorRangeIndex);
            ResourceBindingType expected = GetExpectedResourceBindingType(reflected, category);
            if (nativeUnbounded)
                continue;
            string? diagnostic = DiagnoseBoundedDescriptorRange(
                layout,
                rangeIndex,
                checked((uint)nativeCount),
                fieldName,
                expected,
                bindings,
                pipeline,
                ref ordinal);
            if (diagnostic is not null)
                return diagnostic;
        }
        return null;
    }

    private static (nint SetIndex, nint FirstDescriptorRangeIndex)
        ValidateDescriptorRangeLocation(
            TypeLayoutReflection contents,
            VariableLayoutReflection layout,
            nint rangeIndex,
            nint descriptorRangeCount,
            nint descriptorSetCount)
    {
        nint setIndex = contents.GetBindingRangeDescriptorSetIndex(rangeIndex);
        nint firstDescriptorRangeIndex =
            contents.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
        nuint setIndexMarker = unchecked((nuint)setIndex);
        nuint firstDescriptorRangeIndexMarker = unchecked((nuint)firstDescriptorRangeIndex);
        if (setIndex < 0 || setIndexMarker == Slang.UnknownSize ||
            setIndexMarker == Slang.UnboundedSize || setIndex >= descriptorSetCount)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang binding range {rangeIndex} on layout '{layout.Name}' references invalid descriptor set {setIndex}.");
        }

        nint setRangeCount = contents.GetDescriptorSetDescriptorRangeCount(setIndex);
        nuint setRangeCountMarker = unchecked((nuint)setRangeCount);
        if (setRangeCount < 0 || setRangeCountMarker == Slang.UnknownSize ||
            setRangeCountMarker == Slang.UnboundedSize || firstDescriptorRangeIndex < 0 ||
            firstDescriptorRangeIndexMarker == Slang.UnknownSize ||
            firstDescriptorRangeIndexMarker == Slang.UnboundedSize ||
            firstDescriptorRangeIndex > setRangeCount ||
            descriptorRangeCount > setRangeCount - firstDescriptorRangeIndex)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang binding range {rangeIndex} on layout '{layout.Name}' references " +
                "descriptor ranges outside its descriptor set.");
        }
        return (setIndex, firstDescriptorRangeIndex);
    }

    private static ResourceBindingType GetExpectedResourceBindingType(
        SlangBindingType reflected,
        SlangParameterCategory category)
    {
        SlangBindingType type = reflected & SlangBindingType.BaseMask;
        return type switch
        {
            SlangBindingType.Sampler => ResourceBindingType.Sampler,
            SlangBindingType.ConstantBuffer => ResourceBindingType.ConstantBuffer,
            SlangBindingType.Texture =>
                (reflected & SlangBindingType.MutableFlag) != 0
                    ? ResourceBindingType.TextureUav
                    : ResourceBindingType.TextureSrv,
            SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer =>
                (reflected & SlangBindingType.MutableFlag) != 0
                    ? ResourceBindingType.BufferUav
                    : ResourceBindingType.BufferSrv,
            SlangBindingType.RayTracingAccelerationStructure =>
                ResourceBindingType.AccelerationStructure,
            SlangBindingType.InputRenderTarget => ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when
                category == SlangParameterCategory.ShaderResource =>
                ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when
                category == SlangParameterCategory.SamplerState =>
                ResourceBindingType.Sampler,
            _ => throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang native descriptor binding type {reflected} cannot be " +
                "represented by ResourceBinding."),
        };
    }

    private static string? DiagnoseBoundedDescriptorRange(
        VariableLayoutReflection layout,
        nint rangeIndex,
        uint nativeCount,
        string fieldName,
        ResourceBindingType expected,
        ReadOnlySpan<ResourceBinding> bindings,
        PipelineBindingValidationState? pipeline,
        ref int ordinal)
    {
        for (uint element = 0; element < nativeCount; element++)
        {
            if (expected == ResourceBindingType.Sampler &&
                pipeline?.IsStaticSampler(layout, rangeIndex, element) == true)
            {
                continue;
            }
            if (ordinal >= bindings.Length)
                return $"The Slang parameter layout requires more than {bindings.Length} bounded resource bindings.";
            ref readonly ResourceBinding actual = ref bindings[ordinal];
            if (actual.Type != expected)
            {
                return $"Resource binding {ordinal} for Slang field '{fieldName}' must be {expected}; the supplied binding is {actual.Type}.";
            }
            ordinal++;
        }
        return null;
    }

    private sealed class PipelineBindingValidationState
    {
        private readonly ShaderReflection _reflection;
        private readonly HashSet<VariableLayoutReflection> _layouts = [];
        private readonly HashSet<VariableReflection> _staticSamplers = [];

        internal PipelineBindingValidationState(ShaderReflection reflection)
        {
            _reflection = reflection;
        }

        internal ShaderReflection Reflection => _reflection;

        internal void AddStaticSamplers(ReadOnlySpan<StaticSamplerBinding> samplers)
        {
            _staticSamplers.EnsureCapacity(checked(_staticSamplers.Count + samplers.Length));
            foreach (ref readonly StaticSamplerBinding sampler in samplers)
                _staticSamplers.Add(sampler.Sampler);
        }

        internal void Add(VariableLayoutReflection layout)
        {
            if (layout == VariableLayoutReflection.Null || !_layouts.Add(layout))
                return;
            TypeLayoutReflection contents = GetParameterContentsLayout(layout.TypeLayout);
            nint subObjectRangeCount = contents.SubObjectRangeCount;
            nuint subObjectRangeCountMarker = unchecked((nuint)subObjectRangeCount);
            if (subObjectRangeCount < 0 || subObjectRangeCountMarker == Slang.UnknownSize ||
                subObjectRangeCountMarker == Slang.UnboundedSize)
                throw new GraphicsException(GraphicsError.NativeFailure,
                    $"Slang layout '{layout.Name}' has an unresolved sub-object range count.");
            nint bindingRangeCount = contents.BindingRangeCount;
            nuint bindingRangeCountMarker = unchecked((nuint)bindingRangeCount);
            if (bindingRangeCount < 0 || bindingRangeCountMarker == Slang.UnknownSize ||
                bindingRangeCountMarker == Slang.UnboundedSize)
                throw new GraphicsException(GraphicsError.NativeFailure,
                    $"Slang layout '{layout.Name}' has an unresolved binding-range count.");
            for (nint index = 0; index < subObjectRangeCount; index++)
            {
                nint bindingRange = contents.GetSubObjectRangeBindingRangeIndex(index);
                nuint bindingRangeMarker = unchecked((nuint)bindingRange);
                if (bindingRange < 0 || bindingRangeMarker == Slang.UnknownSize ||
                    bindingRangeMarker == Slang.UnboundedSize ||
                    bindingRange >= bindingRangeCount)
                    throw new GraphicsException(GraphicsError.NativeFailure,
                        $"Slang sub-object range {index} on layout '{layout.Name}' has " +
                        $"invalid binding-range index {bindingRange}.");
                nint occurrenceCount = contents.GetBindingRangeBindingCount(bindingRange);
                nuint occurrenceMarker = unchecked((nuint)occurrenceCount);
                if (occurrenceCount < 0 || occurrenceMarker == Slang.UnknownSize ||
                    occurrenceMarker == Slang.UnboundedSize)
                    throw new GraphicsException(GraphicsError.NativeFailure,
                        $"Slang sub-object binding range {bindingRange} on layout " +
                        $"'{layout.Name}' has an unresolved occurrence count.");
                VariableLayoutReflection child = contents.GetSubObjectRangeOffset(index);
                if (child == VariableLayoutReflection.Null)
                    throw new GraphicsException(GraphicsError.NativeFailure,
                        $"Slang sub-object range {index} on layout '{layout.Name}' has no variable layout.");
                Add(child);
            }
        }

        internal bool Contains(VariableLayoutReflection layout) => _layouts.Contains(layout);
        internal bool IsStaticSampler(
            VariableLayoutReflection rootLayout,
            nint range,
            uint element)
        {
            if (element != 0 || rootLayout == VariableLayoutReflection.Null)
                return false;
            TypeLayoutReflection contents = GetParameterContentsLayout(rootLayout.TypeLayout);
            VariableReflection declaration = contents.GetBindingRangeLeafVariable(range);
            return declaration != VariableReflection.Null && _staticSamplers.Contains(declaration);
        }
    }
}
