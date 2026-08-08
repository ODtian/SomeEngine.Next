using SlangShaderSharp;

namespace SomeEngine.Graphics;

internal readonly record struct ParameterBindingLeaf(
    VariableLayoutReflection Layout,
    ResourceBindingType Type,
    SlangParameterCategory Category,
    uint Binding,
    uint Space,
    uint DescriptorCount,
    bool Unbounded);

internal sealed class ParameterBindingContract
{
    private ParameterBindingContract(
        VariableLayoutReflection layout,
        ParameterBindingLeaf[] leaves,
        uint ordinaryDataSize,
        uint ordinaryRegister,
        uint ordinarySpace,
        int boundedBindingCount)
    {
        Layout = layout;
        Leaves = leaves;
        OrdinaryDataSize = ordinaryDataSize;
        OrdinaryRegister = ordinaryRegister;
        OrdinarySpace = ordinarySpace;
        BoundedBindingCount = boundedBindingCount;
    }

    internal VariableLayoutReflection Layout { get; }
    internal ParameterBindingLeaf[] Leaves { get; }
    internal uint OrdinaryDataSize { get; }
    internal uint OrdinaryRegister { get; }
    internal uint OrdinarySpace { get; }
    internal int BoundedBindingCount { get; }

    internal static ParameterBindingContract Compile(VariableLayoutReflection layout)
    {
        if (layout == VariableLayoutReflection.Null)
            throw new ArgumentException("A parameter block requires a Slang layout.", nameof(layout));

        List<ParameterBindingLeaf> leaves = [];
        int boundedBindingCount = 0;
        Visit(
            layout,
            baseBinding: 0,
            baseSpace: 0,
            fieldBinding: false,
            root: true,
            leaves,
            ref boundedBindingCount);

        nuint uniformSize = GetOrdinarySize(layout);
        if (uniformSize == Slang.UnknownSize || uniformSize == Slang.UnboundedSize ||
            uniformSize > uint.MaxValue)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "The selected Slang parameter layout has an unresolved ordinary-data size.");
        }

        uint ordinarySpace = GetSpace(
            layout,
            SlangParameterCategory.ConstantBuffer,
            layout.BindingSpace);
        return new ParameterBindingContract(
            layout,
            [.. leaves],
            checked((uint)uniformSize),
            layout.BindingIndex,
            ordinarySpace,
            boundedBindingCount);
    }

    internal string? Diagnose(
        ReadOnlySpan<ResourceBinding> bindings,
        ReadOnlySpan<byte> ordinaryData)
    {
        if (ordinaryData.Length != OrdinaryDataSize)
        {
            return $"The parameter block requires exactly {OrdinaryDataSize} " +
                "ordinary-data bytes.";
        }
        if (bindings.Length != BoundedBindingCount)
        {
            return $"The parameter block requires exactly {BoundedBindingCount} " +
                "bounded resource bindings.";
        }

        int ordinal = 0;
        foreach (ParameterBindingLeaf leaf in Leaves)
        {
            if (leaf.Unbounded)
                continue;
            for (uint element = 0; element < leaf.DescriptorCount; element++)
            {
                ref readonly ResourceBinding binding = ref bindings[ordinal];
                if (binding.Type != leaf.Type)
                {
                    return $"Resource binding {ordinal} for Slang field '{leaf.Layout.Name}' " +
                        $"must be {leaf.Type}; the supplied binding is {binding.Type}.";
                }
                if (binding.ArrayElement != element)
                {
                    return $"Resource binding {ordinal} for Slang field '{leaf.Layout.Name}' " +
                        $"must be array element {element}; the supplied element is " +
                        $"{binding.ArrayElement}.";
                }
                ordinal++;
            }
        }
        return null;
    }

    internal void RequireMaterializationShape(
        ReadOnlySpan<ResourceBinding> bindings,
        ReadOnlySpan<byte> ordinaryData)
    {
        if (ordinaryData.Length != OrdinaryDataSize)
        {
            throw new ArgumentException(
                $"The parameter block requires exactly {OrdinaryDataSize} ordinary-data bytes.",
                nameof(ordinaryData));
        }
        if (bindings.Length != BoundedBindingCount)
        {
            throw new ArgumentException(
                $"The parameter block requires exactly {BoundedBindingCount} bounded resource bindings.",
                nameof(bindings));
        }
    }

    private static void Visit(
        VariableLayoutReflection layout,
        uint baseBinding,
        uint baseSpace,
        bool fieldBinding,
        bool root,
        List<ParameterBindingLeaf> leaves,
        ref int boundedBindingCount)
    {
        if (layout == VariableLayoutReflection.Null)
            return;

        TypeLayoutReflection typeLayout = layout.TypeLayout;
        TypeReflection declaredType = layout.Type;
        TypeReflection type = declaredType.UnwrapArray();
        bool blockBoundary = type.Kind is SlangTypeKind.ConstantBuffer or
            SlangTypeKind.ParameterBlock;
        if (!root && blockBoundary)
            return;

        if (!blockBoundary && TryDescribeResource(
                layout,
                type,
                out ResourceBindingType bindingType,
                out SlangParameterCategory category))
        {
            nuint reflectedCount = declaredType.IsArray
                ? declaredType.TotalArrayElementCount
                : 1;
            bool unbounded = reflectedCount == Slang.UnboundedSize;
            if (reflectedCount == Slang.UnknownSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang resource '{layout.Name}' has an unresolved array count.");
            }
            if (!unbounded && (reflectedCount == 0 || reflectedCount > uint.MaxValue))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang resource '{layout.Name}' has an invalid descriptor count.");
            }

            uint descriptorCount = unbounded ? 0 : checked((uint)reflectedCount);
            leaves.Add(new ParameterBindingLeaf(
                layout,
                bindingType,
                category,
                fieldBinding ? checked(baseBinding + layout.BindingIndex) : layout.BindingIndex,
                GetSpace(layout, category, baseSpace),
                descriptorCount,
                unbounded));
            if (!unbounded)
            {
                boundedBindingCount = checked(
                    boundedBindingCount + checked((int)descriptorCount));
            }
            return;
        }

        TypeLayoutReflection fields = FieldsOf(typeLayout);
        if (fields == TypeLayoutReflection.Null)
            return;
        uint childBaseBinding = fieldBinding
            ? baseBinding
            : checked(baseBinding + layout.BindingIndex);
        uint childBaseSpace = GetSpace(
            layout,
            SlangParameterCategory.DescriptorTableSlot,
            baseSpace);
        for (uint index = 0; index < fields.FieldCount; index++)
        {
            Visit(
                fields.GetFieldByIndex(index),
                childBaseBinding,
                childBaseSpace,
                fieldBinding: true,
                root: false,
                leaves,
                ref boundedBindingCount);
        }
    }

    private static TypeLayoutReflection FieldsOf(TypeLayoutReflection layout)
    {
        TypeLayoutReflection unwrapped = layout.UnwrapArray();
        if (unwrapped.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            TypeLayoutReflection element = unwrapped.ElementTypeLayout.UnwrapArray();
            if (element != TypeLayoutReflection.Null)
                return element;
        }
        return unwrapped;
    }

    private static nuint GetOrdinarySize(VariableLayoutReflection layout)
    {
        TypeLayoutReflection type = layout.TypeLayout.UnwrapArray();
        if (type.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            TypeLayoutReflection element = type.ElementTypeLayout.UnwrapArray();
            if (element != TypeLayoutReflection.Null)
                type = element;
        }
        return type.GetSize(SlangParameterCategory.Uniform);
    }

    private static bool TryDescribeResource(
        VariableLayoutReflection layout,
        TypeReflection type,
        out ResourceBindingType bindingType,
        out SlangParameterCategory category)
    {
        category = layout.Category;
        if (category == SlangParameterCategory.DescriptorTableSlot)
            category = SlangParameterCategory.None;

        if (category == SlangParameterCategory.SamplerState ||
            type.Kind == SlangTypeKind.SamplerState)
        {
            bindingType = ResourceBindingType.Sampler;
            category = SlangParameterCategory.SamplerState;
            return true;
        }

        bool writable = category == SlangParameterCategory.UnorderedAccess ||
            IsWritable(type.ResourceAccess);
        bool resource = category is SlangParameterCategory.ShaderResource or
            SlangParameterCategory.UnorderedAccess or SlangParameterCategory.Subpass ||
            type.Kind is SlangTypeKind.Resource or SlangTypeKind.TextureBuffer or
                SlangTypeKind.ShaderStorageBuffer or SlangTypeKind.Feedback or
                SlangTypeKind.DynamicResource;
        if (!resource)
        {
            bindingType = default;
            return false;
        }

        SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;
        bool buffer = shape is SlangResourceShape.ByteAddressBuffer or
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer;
        if (shape == SlangResourceShape.AccelerationStructure)
        {
            bindingType = ResourceBindingType.AccelerationStructure;
            writable = false;
        }
        else if (buffer)
        {
            bindingType = writable
                ? ResourceBindingType.BufferUav
                : ResourceBindingType.BufferSrv;
        }
        else
        {
            bindingType = writable
                ? ResourceBindingType.TextureUav
                : ResourceBindingType.TextureSrv;
        }
        category = writable
            ? SlangParameterCategory.UnorderedAccess
            : SlangParameterCategory.ShaderResource;
        return true;
    }

    private static bool IsWritable(SlangResourceAccess access) => access is
        SlangResourceAccess.Write or SlangResourceAccess.ReadWrite or
        SlangResourceAccess.RasterOrdered or SlangResourceAccess.Append or
        SlangResourceAccess.Consume or SlangResourceAccess.Feedback;

    private static uint GetSpace(
        VariableLayoutReflection layout,
        SlangParameterCategory category,
        uint fallback)
    {
        nuint reflected = layout.GetBindingSpace(category);
        if (reflected > uint.MaxValue)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang layout '{layout.Name}' has an invalid register space.");
        }
        uint space = checked((uint)reflected);
        if (space == 0 && layout.BindingSpace != 0)
            space = layout.BindingSpace;
        return space == 0 ? fallback : space;
    }
}

internal sealed class ParameterBindingContractSet
{
    private readonly Dictionary<VariableLayoutReflection, ParameterBindingContract> _contracts;

    internal ParameterBindingContractSet(IEnumerable<ParameterBindingContract> contracts)
    {
        _contracts = [];
        foreach (ParameterBindingContract contract in contracts)
            _contracts.TryAdd(contract.Layout, contract);
    }

    internal bool TryGet(
        VariableLayoutReflection layout,
        out ParameterBindingContract contract) =>
        _contracts.TryGetValue(layout, out contract!);
}
