using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    private readonly record struct ValidationBindingElement(
        VariableLayoutReflection Field,
        ResourceBindingType Type,
        uint ArrayElement);

    private sealed class ValidationParameterBlockLayout
    {
        private readonly ValidationBindingElement[] _elements;

        private ValidationParameterBlockLayout(
            ParameterBlockLayoutReflection reflection,
            ValidationBindingElement[] elements)
        {
            Reflection = reflection;
            _elements = elements;
        }

        internal ParameterBlockLayoutReflection Reflection { get; }
        internal VariableLayoutReflection Layout => Reflection.Layout;

        internal static ValidationParameterBlockLayout Reflect(
            VariableLayoutReflection layout)
        {
            ParameterBlockLayoutReflection reflection =
                ParameterBlockLayoutReflection.Reflect(layout);
            ReadOnlySpan<ParameterBindingRangeReflection> ranges =
                reflection.BindingRanges;
            ReadOnlySpan<ParameterBindingElementReflection> reflectedElements =
                reflection.BindingElements;
            var elements = new ValidationBindingElement[reflectedElements.Length];
            for (int index = 0; index < elements.Length; index++)
            {
                ref readonly ParameterBindingElementReflection element =
                    ref reflectedElements[index];
                ref readonly ParameterBindingRangeReflection range =
                    ref ranges[element.RangeIndex];
                elements[index] = new ValidationBindingElement(
                    element.Field,
                    ToResourceBindingType(range),
                    element.ArrayElement);
            }
            return new ValidationParameterBlockLayout(reflection, elements);
        }

        internal string? Diagnose(
            ReadOnlySpan<ResourceBinding> bindings,
            ReadOnlySpan<byte> ordinaryData)
        {
            if (ordinaryData.Length != Reflection.OrdinaryDataSize)
            {
                return $"The Slang parameter layout requires exactly " +
                    $"{Reflection.OrdinaryDataSize} ordinary-data bytes.";
            }
            if (bindings.Length != _elements.Length)
            {
                return $"The Slang parameter layout requires exactly {_elements.Length} " +
                    "bounded resource bindings.";
            }

            for (int ordinal = 0; ordinal < _elements.Length; ordinal++)
            {
                ref readonly ValidationBindingElement expected = ref _elements[ordinal];
                ref readonly ResourceBinding actual = ref bindings[ordinal];
                if (actual.Type != expected.Type)
                {
                    return $"Resource binding {ordinal} for Slang field " +
                        $"'{expected.Field.Name}' must be {expected.Type}; the supplied " +
                        $"binding is {actual.Type}.";
                }
                if (actual.ArrayElement != expected.ArrayElement)
                {
                    return $"Resource binding {ordinal} for Slang field " +
                        $"'{expected.Field.Name}' must be array element " +
                        $"{expected.ArrayElement}; the supplied element is " +
                        $"{actual.ArrayElement}.";
                }
            }
            return null;
        }

        private static ResourceBindingType ToResourceBindingType(
            in ParameterBindingRangeReflection range)
        {
            SlangBindingType type = range.Type & SlangBindingType.BaseMask;
            bool writable = (range.Type & SlangBindingType.MutableFlag) != 0;
            return type switch
            {
                SlangBindingType.Sampler => ResourceBindingType.Sampler,
                SlangBindingType.Texture => writable
                    ? ResourceBindingType.TextureUav
                    : ResourceBindingType.TextureSrv,
                SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer => writable
                    ? ResourceBindingType.BufferUav
                    : ResourceBindingType.BufferSrv,
                SlangBindingType.RayTracingAccelerationStructure =>
                    ResourceBindingType.AccelerationStructure,
                _ => throw new InvalidOperationException(
                    $"Slang binding type {range.Type} for field '{range.Field.Name}' " +
                    "cannot be represented as an RHI ResourceBinding."),
            };
        }
    }

    private sealed class PipelineBindingValidationState
    {
        private readonly Dictionary<VariableLayoutReflection, ValidationParameterBlockLayout>
            _layouts = [];

        internal void Add(VariableLayoutReflection layout)
        {
            if (layout == VariableLayoutReflection.Null || _layouts.ContainsKey(layout))
                return;
            _layouts.Add(layout, ValidationParameterBlockLayout.Reflect(layout));
        }

        internal bool TryGet(
            VariableLayoutReflection layout,
            out ValidationParameterBlockLayout reflected) =>
            _layouts.TryGetValue(layout, out reflected!);
    }
}
