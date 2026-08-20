using System.Collections.Frozen;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    // Pipeline-cache serialization schema only; never used for runtime compatibility.
    private const uint RootSignatureSchemaVersion = 4;

    private enum ParameterHeap : byte
    {
        Resource,
        Sampler,
    }

    private static TypeLayoutReflection GetParameterDataLayout(VariableLayoutReflection layout)
    {
            TypeLayoutReflection typeLayout = layout.TypeLayout;
            if (typeLayout == TypeLayoutReflection.Null)
                throw new GraphicsException(GraphicsError.NativeFailure, "Slang returned a null type layout.");

            TypeLayoutReflection dataLayout = typeLayout.UnwrapArray();
            if (dataLayout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            {
                TypeLayoutReflection element = dataLayout.ElementTypeLayout.UnwrapArray();
                if (element != TypeLayoutReflection.Null)
                    dataLayout = element;
            }
            return dataLayout;
    }

    private static uint GetOrdinaryDataSize(VariableLayoutReflection layout)
    {
            nuint ordinarySize = GetParameterDataLayout(layout).GetSize(SlangParameterCategory.Uniform);
            if (ordinarySize == Slang.UnknownSize ||
                ordinarySize == Slang.UnboundedSize ||
                ordinarySize > uint.MaxValue)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang parameter layout '{layout.Name}' has an unresolved ordinary-data size.");
            }
            return checked((uint)ordinarySize);
    }

    private static SlangBindingType GetOrdinaryDataBindingType(
        VariableLayoutReflection layout,
        SlangBindingType reflectedOrdinaryBindingType = SlangBindingType.Unknown)
    {
            uint ordinaryDataSize = GetOrdinaryDataSize(layout);
            TypeLayoutReflection typeLayout = layout.TypeLayout;
            SlangBindingType ordinaryDataBindingType = ordinaryDataSize == 0
                ? SlangBindingType.Unknown
                : ResolveOrdinaryDataBindingType(
                    typeLayout,
                    reflectedOrdinaryBindingType);
            if (ordinaryDataSize != 0 &&
                ordinaryDataBindingType is not (
                    SlangBindingType.ConstantBuffer or
                    SlangBindingType.InlineUniformData or
                    SlangBindingType.PushConstant))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang produced unsupported ordinary-data binding type " +
                    $"{ordinaryDataBindingType} for layout '{layout.Name}'.");
            }
            return ordinaryDataBindingType;
    }

        private static SlangBindingType ResolveOrdinaryDataBindingType(
            TypeLayoutReflection typeLayout,
            SlangBindingType reflected)
        {
            SlangBindingType reflectedBase = reflected & SlangBindingType.BaseMask;
            if (reflectedBase is SlangBindingType.ConstantBuffer or
                SlangBindingType.InlineUniformData or SlangBindingType.PushConstant)
            {
                return reflectedBase;
            }

            if (UsesCategory(typeLayout, SlangParameterCategory.PushConstantBuffer) ||
                UsesCategory(
                    typeLayout.ContainerVarLayout,
                    SlangParameterCategory.PushConstantBuffer))
            {
                return SlangBindingType.PushConstant;
            }
            if (UsesCategory(typeLayout, SlangParameterCategory.ConstantBuffer) ||
                UsesCategory(
                    typeLayout.ContainerVarLayout,
                    SlangParameterCategory.ConstantBuffer))
            {
                return SlangBindingType.ConstantBuffer;
            }
            return SlangBindingType.Unknown;
        }

        private static (uint Register, uint Space) ResolveOrdinaryDataLocation(
            VariableLayoutReflection layout,
            TypeLayoutReflection typeLayout,
            SlangBindingType bindingType,
            uint registerSpaceBase)
        {
            VariableLayoutReflection container = typeLayout.ContainerVarLayout;
            SlangParameterCategory category =
                UsesCategory(layout, SlangParameterCategory.PushConstantBuffer) ||
                UsesCategory(container, SlangParameterCategory.PushConstantBuffer)
                    ? SlangParameterCategory.PushConstantBuffer
                    : SlangParameterCategory.ConstantBuffer;
            if (bindingType == SlangBindingType.PushConstant &&
                category != SlangParameterCategory.PushConstantBuffer)
            {
                category = SlangParameterCategory.PushConstantBuffer;
            }

            nuint layoutRegister = layout.GetOffset(category);
            nuint containerRegister = container.GetOffset(category);
            nuint layoutSpace = layout.GetBindingSpace(category);
            nuint containerSpace = container.GetBindingSpace(category);
            if (IsUnresolved(layoutRegister) || IsUnresolved(containerRegister) ||
                IsUnresolved(layoutSpace) || IsUnresolved(containerSpace) ||
                layoutRegister > uint.MaxValue || containerRegister > uint.MaxValue ||
                layoutSpace > uint.MaxValue || containerSpace > uint.MaxValue ||
                layoutRegister > uint.MaxValue - containerRegister ||
                layoutSpace > uint.MaxValue - containerSpace ||
                layoutSpace + containerSpace > uint.MaxValue - registerSpaceBase)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang ordinary-data location for layout '{layout.Name}' contains an " +
                    "unresolved or out-of-range register/space value.");
            }
            return (
                checked((uint)(layoutRegister + containerRegister)),
                checked(registerSpaceBase + (uint)(layoutSpace + containerSpace)));

            static bool IsUnresolved(nuint value) =>
                value == Slang.UnknownSize || value == Slang.UnboundedSize;
        }

        private static bool UsesCategory(
            TypeLayoutReflection layout,
            SlangParameterCategory category)
        {
            for (uint index = 0; index < layout.CategoryCount; index++)
            {
                if (layout.GetCategoryByIndex(index) == category)
                    return true;
            }
            return false;
        }

        private static bool UsesCategory(
            VariableLayoutReflection layout,
            SlangParameterCategory category)
        {
            for (uint index = 0; index < layout.CategoryCount; index++)
            {
                if (layout.GetCategoryByIndex(index) == category)
                    return true;
            }
            return false;
        }

    private static (
            ResourceBindingType Type,
            ParameterHeap Heap,
            DescriptorRangeType RangeType) ToNativeBinding(
                SlangBindingType source,
                SlangParameterCategory category = SlangParameterCategory.None)
        {
            SlangBindingType type = source & SlangBindingType.BaseMask;
            bool writable = (source & SlangBindingType.MutableFlag) != 0;
            return type switch
            {
                SlangBindingType.Sampler =>
                    (ResourceBindingType.Sampler, ParameterHeap.Sampler,
                        DescriptorRangeType.Sampler),
                SlangBindingType.ConstantBuffer =>
                    (ResourceBindingType.ConstantBuffer, ParameterHeap.Resource,
                        DescriptorRangeType.Cbv),
                SlangBindingType.Texture => writable
                    ? (ResourceBindingType.TextureUav, ParameterHeap.Resource,
                        DescriptorRangeType.Uav)
                    : (ResourceBindingType.TextureSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer => writable
                    ? (ResourceBindingType.BufferUav, ParameterHeap.Resource,
                        DescriptorRangeType.Uav)
                    : (ResourceBindingType.BufferSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.RayTracingAccelerationStructure =>
                    (ResourceBindingType.AccelerationStructure, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.InputRenderTarget =>
                    (ResourceBindingType.TextureSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.CombinedTextureSampler when
                    category == SlangParameterCategory.ShaderResource =>
                    (ResourceBindingType.TextureSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.CombinedTextureSampler when
                    category == SlangParameterCategory.SamplerState =>
                    (ResourceBindingType.Sampler, ParameterHeap.Sampler,
                        DescriptorRangeType.Sampler),
                _ => throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang produced descriptor binding type {source}, which has no D3D12 " +
                    "descriptor-range type."),
             };
         }

    private readonly record struct NativeDescriptorRangeFacts(
        ResourceBindingType Type,
        ParameterHeap Heap,
        DescriptorRangeType RangeType,
        uint ShaderRegister,
        uint RegisterSpace,
        uint Count,
        bool Unbounded);

    private sealed class RootDeclaration
    {
        internal RootDeclaration(
            RootParameterType type,
            DescriptorRange1[] ranges,
            RootConstants constants,
            RootDescriptor1 descriptor,
            ParameterHeap heap,
            bool unbounded,
            ShaderVisibility visibility)
        {
            Type = type;
            Ranges = ranges;
            Constants = constants;
            Descriptor = descriptor;
            Heap = heap;
            Unbounded = unbounded;
            Visibility = visibility;
        }

        internal RootParameterType Type { get; }
        internal DescriptorRange1[] Ranges { get; }
        internal RootConstants Constants { get; }
        internal RootDescriptor1 Descriptor { get; }
        internal ParameterHeap Heap { get; }
        internal bool Unbounded { get; }
        internal ShaderVisibility Visibility { get; set; }
        internal uint RootParameterIndex { get; set; }
        internal uint RootArgumentOffset { get; set; }

    }

    private readonly record struct D3D12BoundedTable(
        uint RootParameterIndex,
        uint RootArgumentOffset,
        uint DescriptorCount);

    private readonly record struct OrdinaryRootBinding(
        uint RootParameterIndex,
        uint RootArgumentOffset,
        bool UsesRootConstants,
        uint ConstantCount,
        uint DataSize);

    private readonly record struct IndirectRootDestination(
        uint RootParameterIndex,
        uint DestinationDwordOffset);

    private sealed class NativeParameterBinding
    {
        internal NativeParameterBinding(
            D3D12BoundedTable? resourceTable,
            D3D12BoundedTable? samplerTable,
            OrdinaryRootBinding? ordinaryRoot,
            uint registerSpaceBase,
            DescriptorSlotDesc[] slots)
        {
            ResourceTable = resourceTable;
            SamplerTable = samplerTable;
            OrdinaryRoot = ordinaryRoot;
            RegisterSpaceBase = registerSpaceBase;
            Slots = slots;
            RootStateLength = GetRootStateLength(
                resourceTable,
                samplerTable,
                ordinaryRoot);
        }

        internal D3D12BoundedTable? ResourceTable { get; }
        internal D3D12BoundedTable? SamplerTable { get; }
        internal OrdinaryRootBinding? OrdinaryRoot { get; }
        internal uint RegisterSpaceBase { get; }
        internal DescriptorSlotDesc[] Slots { get; }
        internal int RootStateLength { get; }

        private static int GetRootStateLength(
            D3D12BoundedTable? resourceTable,
            D3D12BoundedTable? samplerTable,
            OrdinaryRootBinding? ordinaryRoot)
        {
            uint maximum = 0;
            bool present = false;
            if (resourceTable is D3D12BoundedTable resource)
            {
                maximum = resource.RootParameterIndex;
                present = true;
            }
            if (samplerTable is D3D12BoundedTable sampler)
            {
                maximum = present
                    ? Math.Max(maximum, sampler.RootParameterIndex)
                    : sampler.RootParameterIndex;
                present = true;
            }
            if (ordinaryRoot is OrdinaryRootBinding ordinary)
            {
                maximum = present
                    ? Math.Max(maximum, ordinary.RootParameterIndex)
                    : ordinary.RootParameterIndex;
                present = true;
            }
            int result = present ? checked((int)maximum + 1) : 0;
            if (result > 64)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "The D3D12 parameter binding exceeds the root-state slot limit.");
            }
            return result;
        }

    }

    private static void RequireNativeParameterBindings(
        VariableLayoutReflection reflectedLayout,
        NativeParameterBinding placement,
        ReadOnlySpan<ResourceBinding> bindings,
        ReadOnlySpan<byte> ordinaryData)
    {
        uint ordinarySize = placement.OrdinaryRoot?.DataSize ?? 0;
        if (ordinaryData.Length != ordinarySize)
        {
            throw new ArgumentException(
                $"The Slang parameter layout requires exactly {ordinarySize} " +
                $"ordinary-data bytes; the supplied packet has {ordinaryData.Length} bytes.",
                nameof(ordinaryData));
        }
        if (bindings.Length != placement.Slots.Length)
        {
            throw new ArgumentException(
                $"The Slang parameter layout requires exactly {placement.Slots.Length} bounded " +
                $"resource bindings; the supplied packet has {bindings.Length} bindings.",
                nameof(bindings));
        }

        uint resourceIndex = 0;
        uint samplerIndex = 0;
        for (int ordinal = 0; ordinal < placement.Slots.Length; ordinal++)
        {
            ref readonly DescriptorSlotDesc slot = ref placement.Slots[ordinal];
            RequireResourceBinding(bindings[ordinal], slot.Type, ordinal);
            if (slot.Type == ResourceBindingType.Sampler)
                samplerIndex++;
            else
                resourceIndex++;
        }

        uint placedResources = placement.ResourceTable?.DescriptorCount ?? 0;
        uint placedSamplers = placement.SamplerTable?.DescriptorCount ?? 0;
        if (resourceIndex != placedResources || samplerIndex != placedSamplers)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"The D3D12 placement for Slang layout '{reflectedLayout.Name}' contains " +
                $"{placedResources} resource and {placedSamplers} sampler descriptors, but " +
                $"raw reflection resolves {resourceIndex} and {samplerIndex}.");
        }
    }

    private static void RequireResourceBinding(
        in ResourceBinding binding,
        ResourceBindingType expectedType,
        int ordinal)
    {
        if (binding.Type != expectedType)
        {
            throw new ArgumentException(
                $"Resource binding {ordinal} must be {expectedType}; the supplied " +
                $"binding is {binding.Type}.",
                nameof(binding));
        }
        object? value = binding.Value;
        if (value is null)
        {
            if (expectedType == ResourceBindingType.Sampler)
            {
                throw new ArgumentException(
                    $"Resource binding {ordinal} is a Sampler and must provide a concrete " +
                    "Sampler. D3D12 has no null sampler descriptor.",
                    nameof(binding));
            }
            return;
        }
        bool valid = expectedType switch
        {
            ResourceBindingType.ConstantBuffer => value is BufferCbv,
            ResourceBindingType.BufferSrv => value is BufferSrv,
            ResourceBindingType.BufferUav => value is BufferUav,
            ResourceBindingType.TextureSrv => value is TextureSrv,
            ResourceBindingType.TextureUav => value is TextureUav,
            ResourceBindingType.Sampler => value is Sampler,
            ResourceBindingType.AccelerationStructure => value is AccelerationStructureSrv,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Resource binding {ordinal} has value family " +
                $"'{value.GetType().Name}', which cannot represent {expectedType}.",
                nameof(binding));
        }
    }

    private static NativeDescriptorRangeFacts ResolveNativeDescriptorRangeFacts(
        VariableLayoutReflection layout,
        TypeLayoutReflection contents,
        nint bindingRangeIndex,
        nint setIndex,
        nint descriptorRangeIndex,
        bool logicalUnbounded,
        uint registerSpaceBase)
    {
        (uint descriptorCount, bool nativeUnbounded) = ResolveNativeDescriptorCount(
            layout,
            contents,
            bindingRangeIndex,
            setIndex,
            descriptorRangeIndex,
            logicalUnbounded);

        SlangBindingType descriptorType =
            contents.GetDescriptorSetDescriptorRangeType(setIndex, descriptorRangeIndex);
        SlangParameterCategory descriptorCategory =
            contents.GetDescriptorSetDescriptorRangeCategory(setIndex, descriptorRangeIndex);
        (ResourceBindingType type, ParameterHeap heap, DescriptorRangeType rangeType) =
            ToNativeBinding(descriptorType, descriptorCategory);
        uint shaderRegister = ResolveNativeShaderRegister(
            layout,
            contents,
            setIndex,
            descriptorRangeIndex,
            descriptorCategory,
            bindingRangeIndex);
        uint registerSpace = ResolveNativeRegisterSpace(
            layout,
            contents,
            bindingRangeIndex,
            setIndex,
            descriptorRangeIndex,
            descriptorCategory,
            registerSpaceBase);
        return new NativeDescriptorRangeFacts(
            type,
            heap,
            rangeType,
            shaderRegister,
            registerSpace,
            descriptorCount,
            nativeUnbounded);
    }

    private static (uint Count, bool Unbounded) ResolveNativeDescriptorCount(
        VariableLayoutReflection layout,
        TypeLayoutReflection contents,
        nint bindingRangeIndex,
        nint setIndex,
        nint descriptorRangeIndex,
        bool logicalUnbounded)
    {
        nint descriptorCountValue = contents.GetDescriptorSetDescriptorRangeDescriptorCount(
            setIndex,
            descriptorRangeIndex);
        nuint descriptorCountMarker = unchecked((nuint)descriptorCountValue);
        bool nativeUnbounded = descriptorCountMarker == Slang.UnboundedSize;
        if ((!nativeUnbounded &&
             (descriptorCountValue <= 0 || descriptorCountMarker == Slang.UnknownSize ||
              descriptorCountMarker > uint.MaxValue)) ||
            nativeUnbounded != logicalUnbounded)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang descriptor range {descriptorRangeIndex} for binding range " +
                $"{bindingRangeIndex} on layout '{layout.Name}' has invalid descriptor " +
                $"count {descriptorCountValue}.");
        }
        return (
            nativeUnbounded ? uint.MaxValue : checked((uint)descriptorCountValue),
            nativeUnbounded);
    }

    private static uint ResolveNativeShaderRegister(
        VariableLayoutReflection layout,
        TypeLayoutReflection contents,
        nint setIndex,
        nint descriptorRangeIndex,
        SlangParameterCategory descriptorCategory,
        nint bindingRangeIndex)
    {
        nint registerValue = contents.GetDescriptorSetDescriptorRangeIndexOffset(
            setIndex,
            descriptorRangeIndex);
        nuint registerMarker = unchecked((nuint)registerValue);
        nuint layoutRegister = layout.GetOffset(descriptorCategory);
        if (registerValue < 0 || registerMarker == Slang.UnknownSize ||
            registerMarker == Slang.UnboundedSize || registerMarker > uint.MaxValue ||
            layoutRegister == Slang.UnknownSize || layoutRegister == Slang.UnboundedSize ||
            layoutRegister > uint.MaxValue || registerMarker > uint.MaxValue - layoutRegister)
        {
            ThrowInvalidNativeDescriptorLocation(layout, bindingRangeIndex, descriptorRangeIndex);
        }
        return checked((uint)(registerMarker + layoutRegister));
    }

    private static uint ResolveNativeRegisterSpace(
        VariableLayoutReflection layout,
        TypeLayoutReflection contents,
        nint bindingRangeIndex,
        nint setIndex,
        nint descriptorRangeIndex,
        SlangParameterCategory descriptorCategory,
        uint registerSpaceBase)
    {
        nint spaceValue = contents.GetDescriptorSetSpaceOffset(setIndex);
        nuint spaceMarker = unchecked((nuint)spaceValue);
        nuint layoutSpace = layout.GetBindingSpace(descriptorCategory);
        nuint leafSpace = GetDescriptorSubObjectCategorySpace(
            contents,
            bindingRangeIndex,
            descriptorCategory,
            layout.Name);
        if (spaceValue < 0 || spaceMarker == Slang.UnknownSize ||
            spaceMarker == Slang.UnboundedSize || spaceMarker > uint.MaxValue ||
            layoutSpace == Slang.UnknownSize || layoutSpace == Slang.UnboundedSize ||
            layoutSpace > uint.MaxValue || spaceMarker > uint.MaxValue - layoutSpace ||
            leafSpace == Slang.UnknownSize || leafSpace == Slang.UnboundedSize ||
            leafSpace > uint.MaxValue ||
            spaceMarker + layoutSpace > uint.MaxValue - leafSpace ||
            spaceMarker + layoutSpace + leafSpace > uint.MaxValue - registerSpaceBase)
        {
            ThrowInvalidNativeDescriptorLocation(layout, bindingRangeIndex, descriptorRangeIndex);
        }
        return checked(registerSpaceBase + checked((uint)(spaceMarker + layoutSpace + leafSpace)));
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowInvalidNativeDescriptorLocation(
        VariableLayoutReflection layout,
        nint bindingRangeIndex,
        nint descriptorRangeIndex)
    {
        throw new GraphicsException(
            GraphicsError.NativeFailure,
            $"Slang descriptor range {descriptorRangeIndex} for binding range " +
            $"{bindingRangeIndex} on layout '{layout.Name}' has an invalid D3D12 " +
            "register or space offset.");
    }

    private static DescriptorSlotDesc ResolveDescriptorSlotDesc(
        TypeLayoutReflection contents,
        nint bindingRangeIndex,
        ResourceBindingType type)
    {
        TypeLayoutReflection leaf =
            contents.GetBindingRangeLeafTypeLayout(bindingRangeIndex).UnwrapArray();
        if (leaf == TypeLayoutReflection.Null)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang binding range {bindingRangeIndex} has no leaf type layout.");
        }

        return type switch
        {
            ResourceBindingType.ConstantBuffer or
            ResourceBindingType.Sampler or
            ResourceBindingType.AccelerationStructure => new DescriptorSlotDesc(type),
            ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav =>
                ResolveBufferSlot(leaf, type),
            ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav =>
                new DescriptorSlotDesc(
                    type,
                    ResolveCanonicalResourceFormat(leaf.ResourceResultType),
                    TextureDimension: ResolveTextureDimension(leaf.ResourceShape)),
            _ => throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang binding range {bindingRangeIndex} resolved unsupported binding type {type}."),
        };

        static DescriptorSlotDesc ResolveBufferSlot(
            TypeLayoutReflection leaf,
            ResourceBindingType type)
        {
            SlangResourceShape baseShape =
                leaf.ResourceShape & SlangResourceShape.BaseShapeMask;
            bool hasCounter = type == ResourceBindingType.BufferUav &&
                leaf.ExplicitCounter != VariableLayoutReflection.Null;
            return baseShape switch
            {
                SlangResourceShape.StructuredBuffer => new DescriptorSlotDesc(
                    type,
                    StructureStride: ResolveStructuredStride(leaf),
                    HasCounter: hasCounter),
                SlangResourceShape.TextureBuffer => new DescriptorSlotDesc(
                    type,
                    ResolveCanonicalResourceFormat(leaf.ResourceResultType),
                    HasCounter: hasCounter),
                SlangResourceShape.ByteAddressBuffer or
                SlangResourceShape.ResourceUnknown => new DescriptorSlotDesc(
                    type,
                    HasCounter: hasCounter),
                _ => new DescriptorSlotDesc(type, HasCounter: hasCounter),
            };
        }

        static uint ResolveStructuredStride(TypeLayoutReflection resource)
        {
            TypeLayoutReflection element = resource.ElementTypeLayout;
            if (element == TypeLayoutReflection.Null)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "Slang returned no element layout for a structured Buffer.");
            }
            nuint stride = element.GetStride(SlangParameterCategory.Uniform);
            if (stride == 0 || stride == Slang.UnknownSize || stride == Slang.UnboundedSize)
                stride = element.GetSize(SlangParameterCategory.Uniform);
            if (stride == 0 || stride == Slang.UnknownSize || stride == Slang.UnboundedSize ||
                stride > uint.MaxValue || (stride & 3) != 0 || stride > 2_048)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang returned invalid structured-Buffer stride {stride}.");
            }
            return checked((uint)stride);
        }
    }

    private static TextureViewDimension ResolveTextureDimension(SlangResourceShape shape)
    {
        const SlangResourceShape mask =
            SlangResourceShape.BaseShapeMask |
            SlangResourceShape.TextureArrayFlag |
            SlangResourceShape.TextureMultisampleFlag;
        return (shape & mask) switch
        {
            SlangResourceShape.Texture1D => TextureViewDimension.Texture1D,
            SlangResourceShape.Texture1DArray => TextureViewDimension.Texture1DArray,
            SlangResourceShape.Texture2D => TextureViewDimension.Texture2D,
            SlangResourceShape.Texture2DArray => TextureViewDimension.Texture2DArray,
            SlangResourceShape.Texture2DMultisample =>
                TextureViewDimension.Texture2DMultisampled,
            SlangResourceShape.Texture2DMultisampleArray =>
                TextureViewDimension.Texture2DMultisampledArray,
            SlangResourceShape.Texture3D => TextureViewDimension.Texture3D,
            SlangResourceShape.TextureCube => TextureViewDimension.Cube,
            SlangResourceShape.TextureCubeArray => TextureViewDimension.CubeArray,
            _ => throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang resource shape {shape} has no portable texture-view dimension."),
        };
    }

    private static Format ResolveCanonicalResourceFormat(TypeReflection resultType)
    {
        if (resultType == TypeReflection.Null)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "Slang returned no result type for a typed resource.");
        }
        uint components = resultType.Kind == SlangTypeKind.Vector
            ? Math.Max(resultType.RowCount, resultType.ColumnCount)
            : 1;
        if (components == 0 || components > 4)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang resource result type '{resultType.Name}' has unsupported component count {components}.");
        }
        uint normalizedComponents = components == 3 ? 4 : components;
        return (resultType.ScalarType, normalizedComponents) switch
        {
            (SlangScalarType.Bool or SlangScalarType.UInt32, 1) => Format.R32UInt,
            (SlangScalarType.Bool or SlangScalarType.UInt32, 2) => Format.R32G32UInt,
            (SlangScalarType.Bool or SlangScalarType.UInt32, 4) => Format.R32G32B32A32UInt,
            (SlangScalarType.Int32, 1) => Format.R32SInt,
            (SlangScalarType.Int32, 2) => Format.R32G32SInt,
            (SlangScalarType.Int32, 4) => Format.R32G32B32A32SInt,
            (SlangScalarType.Float32, 1) => Format.R32Float,
            (SlangScalarType.Float32, 2) => Format.R32G32Float,
            (SlangScalarType.Float32, 4) => Format.R32G32B32A32Float,
            (SlangScalarType.UInt16, 1) => Format.R16UInt,
            (SlangScalarType.UInt16, 2) => Format.R16G16UInt,
            (SlangScalarType.UInt16, 4) => Format.R16G16B16A16UInt,
            (SlangScalarType.Int16, 1) => Format.R16SInt,
            (SlangScalarType.Int16, 2) => Format.R16G16SInt,
            (SlangScalarType.Int16, 4) => Format.R16G16B16A16SInt,
            (SlangScalarType.Float16, 1) => Format.R16Float,
            (SlangScalarType.Float16, 2) => Format.R16G16Float,
            (SlangScalarType.Float16, 4) => Format.R16G16B16A16Float,
            (SlangScalarType.UInt8, 1) => Format.R8UInt,
            (SlangScalarType.UInt8, 2) => Format.R8G8UInt,
            (SlangScalarType.UInt8, 4) => Format.R8G8B8A8UInt,
            (SlangScalarType.Int8, 1) => Format.R8SInt,
            (SlangScalarType.Int8, 2) => Format.R8G8SInt,
            (SlangScalarType.Int8, 4) => Format.R8G8B8A8SInt,
            _ => throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang resource result type '{resultType.Name}' cannot be represented by a current RHI format."),
        };
    }

    private static nuint GetDescriptorSubObjectCategorySpace(
        TypeLayoutReflection layout,
        nint bindingRangeIndex,
        SlangParameterCategory category,
        string layoutName)
    {
        nint subObjectRangeCount = layout.SubObjectRangeCount;
        nuint marker = unchecked((nuint)subObjectRangeCount);
        if (subObjectRangeCount < 0 || marker == Slang.UnknownSize ||
            marker == Slang.UnboundedSize)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang layout '{layoutName}' has an unresolved sub-object range count " +
                "while resolving a descriptor category space.");
        }
        for (nint index = 0; index < subObjectRangeCount; index++)
        {
            if (layout.GetSubObjectRangeBindingRangeIndex(index) != bindingRangeIndex)
                continue;
            VariableLayoutReflection leaf = layout.GetSubObjectRangeOffset(index);
            if (leaf == VariableLayoutReflection.Null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang descriptor binding range {bindingRangeIndex} on layout " +
                    $"'{layoutName}' has no variable layout.");
            }
            return leaf.GetBindingSpace(category);
        }
        return 0;
    }

    private readonly record struct DefaultRootTable(
        uint RootParameterIndex,
        uint RootArgumentOffset,
        ParameterHeap Heap);

    private sealed class D3D12RootSignatureState
    {
        private readonly NativeLease _native;
        private readonly FrozenDictionary<VariableLayoutReflection, NativeParameterBinding>
            _parameterBindings;
        private int _released;

        internal D3D12RootSignatureState(
            ID3D12RootSignature* native,
            ShaderReflection reflection,
            Dictionary<VariableLayoutReflection, NativeParameterBinding> blocks,
            DefaultRootTable[] defaults,
            StaticSamplerDesc[] staticSamplers,
            byte[] serialized,
            uint rootArgumentSize)
        {
            _native = new NativeLease((IUnknown*)native, ownsReference: true);
            Reflection = reflection;
            _parameterBindings = blocks.ToFrozenDictionary();
            DefaultTables = defaults;
            StaticSamplers = staticSamplers;
            Serialized = serialized;
            RootArgumentSize = rootArgumentSize;
            RootStateLength = GetRootStateLength(blocks, defaults);
        }

        internal ID3D12RootSignature* Native =>
            (ID3D12RootSignature*)_native.Pointer;
        internal ShaderReflection Reflection { get; }
        internal NativeLease NativeLifetime => _native;
        internal DefaultRootTable[] DefaultTables { get; }
        internal StaticSamplerDesc[] StaticSamplers { get; }
        internal byte[] Serialized { get; }
        internal uint RootArgumentSize { get; }
        internal int RootStateLength { get; }

        private static int GetRootStateLength(
            Dictionary<VariableLayoutReflection, NativeParameterBinding> blocks,
            DefaultRootTable[] defaults)
        {
            int result = 0;
            foreach (NativeParameterBinding binding in blocks.Values)
                result = Math.Max(result, binding.RootStateLength);
            foreach (ref readonly DefaultRootTable table in defaults.AsSpan())
            {
                result = Math.Max(
                    result,
                    checked((int)table.RootParameterIndex + 1));
            }
            if ((uint)result > 64u)
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "A D3D12 root signature cannot expose more than 64 root parameters.");
            return result;
        }

        internal NativeParameterBinding GetBlock(VariableLayoutReflection layout)
        {
            if (!_parameterBindings.TryGetValue(layout, out NativeParameterBinding? binding))
            {
                throw new ArgumentException(
                    "The Slang parameter layout is not part of the current Pipeline.",
                    nameof(layout));
            }
            return binding;
        }

        internal bool IsStaticSampler(uint shaderRegister, uint registerSpace)
        {
            foreach (ref readonly StaticSamplerDesc sampler in StaticSamplers.AsSpan())
            {
                if (sampler.ShaderRegister == shaderRegister &&
                    sampler.RegisterSpace == registerSpace)
                {
                    return true;
                }
            }
            return false;
        }

        internal IndirectRootDestination ResolveIndirectRoot(
            VariableLayoutReflection parameters,
            IndirectArgumentType type,
            uint byteOffset,
            uint valueCount)
        {
            if (parameters == VariableLayoutReflection.Null)
                throw new ArgumentException("A Slang parameter-object layout is required.", nameof(parameters));
            NativeParameterBinding block = GetBlock(parameters);
            OrdinaryRootBinding ordinary = block.OrdinaryRoot
                ?? throw new NotSupportedException(
                    "The targeted Slang parameter object has no D3D12 root ordinary-data binding.");
            ulong absoluteOffset = byteOffset;
            if ((absoluteOffset & (sizeof(uint) - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(byteOffset),
                    "An indirect ordinary-data offset must be DWORD aligned.");
            }

            switch (type)
            {
                case IndirectArgumentType.Constants:
                {
                    if (!ordinary.UsesRootConstants || valueCount == 0)
                    {
                        throw new NotSupportedException(
                            "The targeted Slang parameter object is not materialized as D3D12 root constants.");
                    }
                    ulong byteCount = checked((ulong)valueCount * sizeof(uint));
                    if (absoluteOffset > ordinary.DataSize ||
                        byteCount > ordinary.DataSize - absoluteOffset)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(valueCount),
                            "The indirect constant range exceeds the targeted Slang ordinary-data region.");
                    }
                    return new IndirectRootDestination(
                        ordinary.RootParameterIndex,
                        checked((uint)(absoluteOffset / sizeof(uint))));
                }
                case IndirectArgumentType.ConstantBuffer:
                    if (ordinary.UsesRootConstants || absoluteOffset != 0 || valueCount != 0)
                    {
                        throw new NotSupportedException(
                            "The target is not the root of a D3D12 root-CBV parameter object.");
                    }
                    return new IndirectRootDestination(ordinary.RootParameterIndex, 0);
                case IndirectArgumentType.ShaderResource:
                case IndirectArgumentType.UnorderedAccess:
                    throw new NotSupportedException(
                        "The current D3D12 layout compiler materializes Slang resource bindings in descriptor tables, so they cannot be changed by ExecuteIndirect root descriptors.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _native.Release();
        }
    }

    private sealed class D3D12RootSignatureBuilder
    {
        private readonly D3D12Backend _backend;
        private readonly D3D12Device _device;
        private readonly ShaderReflection _reflection;
        private readonly PipelineType _pipelineType;
        private readonly bool _allowInputAssembler;
        private readonly bool _allowStreamOutput;
        private readonly bool _localRootSignature;
        private readonly List<RootDeclaration> _declarations = [];
        private readonly Dictionary<VariableReflection, SamplerDesc> _staticSamplers = [];
        private readonly List<StaticSamplerDesc> _nativeStaticSamplers = [];
        private readonly HashSet<VariableReflection> _resolvedStaticSamplers = [];
        private readonly Dictionary<VariableLayoutReflection, NativeParameterBindingBuild> _parameterBindingBuilds = [];
        private readonly HashSet<VariableLayoutReflection> _parameterBindingsInProgress = [];

        private D3D12RootSignatureBuilder(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput,
            bool localRootSignature,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers)
        {
            _backend = backend;
            _device = device;
            _reflection = reflection;
            _pipelineType = pipelineType;
            _allowInputAssembler = allowInputAssembler;
            _allowStreamOutput = allowStreamOutput;
            _localRootSignature = localRootSignature;
            foreach (ref readonly StaticSamplerBinding sampler in staticSamplers)
            {
                if (sampler.Sampler == VariableReflection.Null)
                    throw new GraphicsException(GraphicsError.PipelineCreation,
                        "A static sampler must identify a non-null Slang sampler declaration.");
                if (_staticSamplers.ContainsKey(sampler.Sampler))
                {
                    throw new GraphicsException(
                        GraphicsError.PipelineCreation,
                        $"Static sampler declaration '{sampler.Sampler.Name}' is declared more than once.");
                }
                ValidateStaticSampler(sampler.Description);
                _staticSamplers.Add(sampler.Sampler, sampler.Description);
            }
        }

        internal static D3D12RootSignatureState Compile(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput)
        {
            return CompileCore(
                backend,
                device,
                reflection,
                entryPoints,
                staticSamplers,
                pipelineType,
                allowInputAssembler,
                allowStreamOutput,
                includeGlobal: true,
                includeEntries: true,
                localRootSignature: false,
                requireAllStaticSamplers: true);
        }

        internal static D3D12RootSignatureState CompileGlobal(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers,
            PipelineType pipelineType,
            bool requireAllStaticSamplers = false) =>
            CompileCore(
                backend,
                device,
                reflection,
                ReadOnlySpan<EntryPointReflection>.Empty,
                staticSamplers,
                pipelineType,
                allowInputAssembler: false,
                allowStreamOutput: false,
                includeGlobal: true,
                includeEntries: false,
                localRootSignature: false,
                requireAllStaticSamplers: requireAllStaticSamplers);

        internal static D3D12RootSignatureState CompileLocal(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers,
            PipelineType pipelineType) =>
            CompileCore(
                backend,
                device,
                reflection,
                entryPoints,
                staticSamplers,
                pipelineType,
                allowInputAssembler: false,
                allowStreamOutput: false,
                includeGlobal: false,
                includeEntries: true,
                localRootSignature: true,
                requireAllStaticSamplers: false);

        internal static void ValidateStaticSamplers(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers,
            PipelineType pipelineType)
        {
            if (staticSamplers.IsEmpty)
                return;
            D3D12RootSignatureBuilder builder = new(
                backend,
                device,
                reflection,
                pipelineType,
                allowInputAssembler: false,
                allowStreamOutput: false,
                localRootSignature: false,
                staticSamplers);
            VariableLayoutReflection global = reflection.GetGlobalParamsVarLayout()
                ?? VariableLayoutReflection.Null;
            if (global != VariableLayoutReflection.Null)
                builder.AddBlock(global, ShaderVisibility.All);
            foreach (EntryPointReflection entryPoint in entryPoints)
            {
                if (entryPoint.VarLayout != VariableLayoutReflection.Null)
                    builder.AddBlock(entryPoint.VarLayout, ToNativeVisibility(entryPoint.Stage));
            }
            builder.RequireAllStaticSamplersResolved();
        }

        private static D3D12RootSignatureState CompileCore(
            D3D12Backend backend,
            D3D12Device device,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput,
            bool includeGlobal,
            bool includeEntries,
            bool localRootSignature,
            bool requireAllStaticSamplers)
        {
            D3D12RootSignatureBuilder builder = new(
                backend,
                device,
                reflection,
                pipelineType,
                allowInputAssembler,
                allowStreamOutput,
                localRootSignature,
                staticSamplers);
            if (includeGlobal &&
                reflection.GetGlobalParamsVarLayout() is VariableLayoutReflection global &&
                global != VariableLayoutReflection.Null)
            {
                builder.AddBlock(global, ShaderVisibility.All);
            }
            if (includeEntries)
            {
                foreach (EntryPointReflection entryPoint in entryPoints)
                {
                    ShaderVisibility stage = ToNativeVisibility(entryPoint.Stage);
                    VariableLayoutReflection container = entryPoint.VarLayout;
                    if (container != VariableLayoutReflection.Null)
                        builder.AddBlock(container, stage);
                }
            }
            if (requireAllStaticSamplers)
                builder.RequireAllStaticSamplersResolved();
            return builder.Build();
        }

        private void RequireAllStaticSamplersResolved()
        {
            foreach (var pair in _staticSamplers)
            {
                if (!_resolvedStaticSamplers.Contains(pair.Key))
                {
                    throw new GraphicsException(
                        GraphicsError.PipelineCreation,
                        $"Static sampler declaration '{pair.Key.Name}' " +
                        "does not resolve to a sampler binding in the linked pipeline.");
                }
            }
        }

        private bool HasStaticSamplerInRange(
            TypeLayoutReflection dataLayout,
            nint bindingRangeIndex)
        {
            VariableReflection declaration = dataLayout.GetBindingRangeLeafVariable(bindingRangeIndex);
            return declaration != VariableReflection.Null && _staticSamplers.ContainsKey(declaration);
        }

        private static void ValidateStaticSampler(in SamplerDesc sampler)
        {
            ValidateSampler(sampler);
            _ = ToStaticBorderColor(sampler.BorderColor);
        }

        private void AddBlock(
            VariableLayoutReflection layout,
            ShaderVisibility visibility,
            SlangBindingType reflectedOrdinaryBindingType = SlangBindingType.Unknown,
            uint registerSpaceBase = 0)
        {
            if (layout == VariableLayoutReflection.Null)
                return;
            if (!_parameterBindingsInProgress.Add(layout))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang parameter sub-object traversal contains a cycle at layout " +
                    $"'{layout.Name}'.");
            }

            try
            {
                AddBlockCore(
                    layout,
                    visibility,
                    reflectedOrdinaryBindingType,
                    registerSpaceBase);
            }
            finally
            {
                _parameterBindingsInProgress.Remove(layout);
            }
        }

        private void AddBlockCore(
            VariableLayoutReflection layout,
            ShaderVisibility visibility,
            SlangBindingType reflectedOrdinaryBindingType,
            uint registerSpaceBase)
        {
            if (MergeExistingBlock(layout, visibility, registerSpaceBase))
                return;

            int staticSamplerStart = _nativeStaticSamplers.Count;
            TypeLayoutReflection dataLayout = GetParameterDataLayout(layout);
            uint ordinaryDataSize = GetOrdinaryDataSize(layout);
            RootDeclaration? ordinary = AddOrdinaryRootDeclaration(
                layout,
                visibility,
                reflectedOrdinaryBindingType,
                registerSpaceBase,
                ordinaryDataSize);
            List<DescriptorRange1> resourceRanges = [];
            List<DescriptorRange1> samplerRanges = [];
            List<DescriptorSlotDesc> boundedSlots = [];
            List<RootDeclaration> unboundedTables = [];
            uint resourceCount = 0;
            uint samplerCount = 0;
            AddDescriptorBindings(
                layout,
                dataLayout,
                visibility,
                registerSpaceBase,
                resourceRanges,
                samplerRanges,
                boundedSlots,
                unboundedTables,
                ref resourceCount,
                ref samplerCount);
            RootDeclaration? resourceTable = AddBoundedTable(
                resourceRanges,
                ParameterHeap.Resource,
                visibility);
            RootDeclaration? samplerTable = AddBoundedTable(
                samplerRanges,
                ParameterHeap.Sampler,
                visibility);
            _parameterBindingBuilds.Add(
                layout,
                new NativeParameterBindingBuild(
                resourceTable,
                resourceCount,
                samplerTable,
                samplerCount,
                ordinary,
                ordinaryDataSize,
                registerSpaceBase,
                [.. boundedSlots],
                staticSamplerStart,
                checked(_nativeStaticSamplers.Count - staticSamplerStart),
                [.. unboundedTables, .. (resourceTable is null ? [] : new[] { resourceTable }),
                    .. (samplerTable is null ? [] : new[] { samplerTable }),
                    .. (ordinary is null ? [] : new[] { ordinary })]));
            AddChildBlocks(layout, visibility, registerSpaceBase);
        }

        private bool MergeExistingBlock(
            VariableLayoutReflection layout,
            ShaderVisibility visibility,
            uint registerSpaceBase)
        {
            if (!_parameterBindingBuilds.TryGetValue(
                    layout,
                    out NativeParameterBindingBuild? existing))
            {
                return false;
            }
            if (existing.RegisterSpaceBase != registerSpaceBase)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang layout '{layout.Name}' resolves to more than one D3D12 " +
                    "register-space placement.");
            }
            foreach (RootDeclaration declaration in existing.Declarations)
                declaration.Visibility = MergeVisibility(declaration.Visibility, visibility);
            int staticSamplerEnd = checked(
                existing.StaticSamplerStart + existing.StaticSamplerCount);
            for (int index = existing.StaticSamplerStart; index < staticSamplerEnd; index++)
            {
                StaticSamplerDesc sampler = _nativeStaticSamplers[index];
                sampler.ShaderVisibility = MergeVisibility(sampler.ShaderVisibility, visibility);
                _nativeStaticSamplers[index] = sampler;
            }
            AddChildBlocks(layout, visibility, registerSpaceBase);
            return true;
        }

        private RootDeclaration? AddOrdinaryRootDeclaration(
            VariableLayoutReflection layout,
            ShaderVisibility visibility,
            SlangBindingType reflectedOrdinaryBindingType,
            uint registerSpaceBase,
            uint ordinaryDataSize)
        {
            if (ordinaryDataSize == 0)
                return null;
            SlangBindingType ordinaryType = GetOrdinaryDataBindingType(
                layout,
                reflectedOrdinaryBindingType);
            (uint shaderRegister, uint space) = ResolveOrdinaryDataLocation(
                layout,
                layout.TypeLayout,
                ordinaryType,
                registerSpaceBase);
            bool constants = ordinaryType is
                SlangBindingType.InlineUniformData or SlangBindingType.PushConstant;
            var declaration = new RootDeclaration(
                constants ? RootParameterType.Type32BitConstants : RootParameterType.TypeCbv,
                [],
                constants
                    ? new RootConstants(
                        shaderRegister,
                        space,
                        checked((ordinaryDataSize + sizeof(uint) - 1) / sizeof(uint)))
                    : default,
                constants
                    ? default
                    : new RootDescriptor1(shaderRegister, space, RootDescriptorFlags.DataStatic),
                ParameterHeap.Resource,
                false,
                visibility);
            _declarations.Add(declaration);
            return declaration;
        }

        private void AddDescriptorBindings(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            ShaderVisibility visibility,
            uint registerSpaceBase,
            List<DescriptorRange1> resourceRanges,
            List<DescriptorRange1> samplerRanges,
            List<DescriptorSlotDesc> boundedSlots,
            List<RootDeclaration> unboundedTables,
            ref uint resourceCount,
            ref uint samplerCount)
        {
            nint bindingRangeCount = dataLayout.BindingRangeCount;
            nint descriptorSetCount = dataLayout.DescriptorSetCount;
            ValidateDescriptorContainerCounts(layout, bindingRangeCount, descriptorSetCount);
            for (nint rangeIndex = 0; rangeIndex < bindingRangeCount; rangeIndex++)
            {
                AddDescriptorBindingRange(
                    layout,
                    dataLayout,
                    rangeIndex,
                    descriptorSetCount,
                    visibility,
                    registerSpaceBase,
                    resourceRanges,
                    samplerRanges,
                    boundedSlots,
                    unboundedTables,
                    ref resourceCount,
                    ref samplerCount);
            }
        }

        private static void ValidateDescriptorContainerCounts(
            VariableLayoutReflection layout,
            nint bindingRangeCount,
            nint descriptorSetCount)
        {
            nuint bindingRangeCountMarker = unchecked((nuint)bindingRangeCount);
            nuint descriptorSetCountMarker = unchecked((nuint)descriptorSetCount);
            if (bindingRangeCount < 0 || bindingRangeCountMarker == Slang.UnknownSize ||
                bindingRangeCountMarker == Slang.UnboundedSize || descriptorSetCount < 0 ||
                descriptorSetCountMarker == Slang.UnknownSize ||
                descriptorSetCountMarker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang layout '{layout.Name}' has unresolved binding-range or descriptor-set counts.");
            }
        }

        private void AddDescriptorBindingRange(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint rangeIndex,
            nint descriptorSetCount,
            ShaderVisibility visibility,
            uint registerSpaceBase,
            List<DescriptorRange1> resourceRanges,
            List<DescriptorRange1> samplerRanges,
            List<DescriptorSlotDesc> boundedSlots,
            List<RootDeclaration> unboundedTables,
            ref uint resourceCount,
            ref uint samplerCount)
        {
            nint descriptorRangeCount = GetDescriptorRangeCount(layout, dataLayout, rangeIndex);
            if (descriptorRangeCount == 0)
                return;
            nint countValue = dataLayout.GetBindingRangeBindingCount(rangeIndex);
            nuint countMarker = unchecked((nuint)countValue);
            bool logicalUnbounded = countMarker == Slang.UnboundedSize;
            if (!logicalUnbounded &&
                (countValue <= 0 || countMarker == Slang.UnknownSize || countMarker > uint.MaxValue))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang binding range {rangeIndex} on layout '{layout.Name}' has invalid descriptor count {countValue}.");
            }
            bool rangeHasStaticSampler = HasStaticSamplerInRange(dataLayout, rangeIndex);
            ValidateStaticSamplerRange(
                layout,
                rangeIndex,
                countValue,
                descriptorRangeCount,
                logicalUnbounded,
                rangeHasStaticSampler);
            (nint setIndex, nint firstDescriptorRangeIndex) = ResolveDescriptorRangeLocation(
                layout,
                dataLayout,
                rangeIndex,
                descriptorRangeCount,
                descriptorSetCount);
            for (nint relativeIndex = 0; relativeIndex < descriptorRangeCount; relativeIndex++)
            {
                AddNativeDescriptorBinding(
                    layout,
                    dataLayout,
                    rangeIndex,
                    setIndex,
                    firstDescriptorRangeIndex + relativeIndex,
                    logicalUnbounded,
                    rangeHasStaticSampler,
                    visibility,
                    registerSpaceBase,
                    resourceRanges,
                    samplerRanges,
                    boundedSlots,
                    unboundedTables,
                    ref resourceCount,
                    ref samplerCount);
            }
        }

        private static nint GetDescriptorRangeCount(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint rangeIndex)
        {
            nint descriptorRangeCount = dataLayout.GetBindingRangeDescriptorRangeCount(rangeIndex);
            nuint marker = unchecked((nuint)descriptorRangeCount);
            if (descriptorRangeCount < 0 || marker == Slang.UnknownSize ||
                marker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang binding range {rangeIndex} on layout '{layout.Name}' has an unresolved descriptor-range count.");
            }
            return descriptorRangeCount;
        }

        private static void ValidateStaticSamplerRange(
            VariableLayoutReflection layout,
            nint rangeIndex,
            nint countValue,
            nint descriptorRangeCount,
            bool logicalUnbounded,
            bool rangeHasStaticSampler)
        {
            if (!rangeHasStaticSampler ||
                (!logicalUnbounded && countValue == 1 && descriptorRangeCount == 1))
            {
                return;
            }
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"D3D12 static samplers can replace only one scalar Slang sampler " +
                $"binding range. Range {rangeIndex} on layout '{layout.Name}' has " +
                $"binding count {countValue} and descriptor-range count {descriptorRangeCount}.");
        }

        private static (nint SetIndex, nint FirstDescriptorRangeIndex)
            ResolveDescriptorRangeLocation(
                VariableLayoutReflection layout,
                TypeLayoutReflection dataLayout,
                nint rangeIndex,
                nint descriptorRangeCount,
                nint descriptorSetCount)
        {
            nint setIndex = dataLayout.GetBindingRangeDescriptorSetIndex(rangeIndex);
            nint firstDescriptorRangeIndex =
                dataLayout.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
            nuint setIndexMarker = unchecked((nuint)setIndex);
            nuint firstRangeMarker = unchecked((nuint)firstDescriptorRangeIndex);
            if (setIndex < 0 || setIndexMarker == Slang.UnknownSize ||
                setIndexMarker == Slang.UnboundedSize || setIndex >= descriptorSetCount)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang binding range {rangeIndex} on layout '{layout.Name}' references invalid descriptor set {setIndex} of {descriptorSetCount}.");
            }
            nint setRangeCount = dataLayout.GetDescriptorSetDescriptorRangeCount(setIndex);
            nuint setRangeMarker = unchecked((nuint)setRangeCount);
            if (setRangeCount < 0 || setRangeMarker == Slang.UnknownSize ||
                setRangeMarker == Slang.UnboundedSize || firstDescriptorRangeIndex < 0 ||
                firstRangeMarker == Slang.UnknownSize ||
                firstRangeMarker == Slang.UnboundedSize ||
                firstDescriptorRangeIndex > setRangeCount ||
                descriptorRangeCount > setRangeCount - firstDescriptorRangeIndex)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang binding range {rangeIndex} on layout '{layout.Name}' references " +
                    $"descriptor ranges [{firstDescriptorRangeIndex}, " +
                    $"{firstDescriptorRangeIndex + descriptorRangeCount}) outside set " +
                    $"{setIndex}, which contains {setRangeCount} ranges.");
            }
            return (setIndex, firstDescriptorRangeIndex);
        }

        private void AddNativeDescriptorBinding(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint rangeIndex,
            nint setIndex,
            nint descriptorRangeIndex,
            bool logicalUnbounded,
            bool rangeHasStaticSampler,
            ShaderVisibility visibility,
            uint registerSpaceBase,
            List<DescriptorRange1> resourceRanges,
            List<DescriptorRange1> samplerRanges,
            List<DescriptorSlotDesc> boundedSlots,
            List<RootDeclaration> unboundedTables,
            ref uint resourceCount,
            ref uint samplerCount)
        {
            NativeDescriptorRangeFacts facts = ResolveNativeDescriptorRangeFacts(
                layout,
                dataLayout,
                rangeIndex,
                setIndex,
                descriptorRangeIndex,
                logicalUnbounded,
                registerSpaceBase);
            if (facts.Unbounded)
            {
                AddUnboundedDescriptorTable(facts, visibility, unboundedTables);
                return;
            }
            if (facts.Heap == ParameterHeap.Sampler && rangeHasStaticSampler)
            {
                AddStaticSampler(layout, dataLayout, rangeIndex, facts, visibility);
                return;
            }
            DescriptorSlotDesc slot = ResolveDescriptorSlotDesc(dataLayout, rangeIndex, facts.Type);
            for (uint element = 0; element < facts.Count; element++)
                boundedSlots.Add(slot);
            AddBoundedDescriptorRange(
                facts,
                resourceRanges,
                samplerRanges,
                ref resourceCount,
                ref samplerCount);
        }

        private void AddUnboundedDescriptorTable(
            in NativeDescriptorRangeFacts facts,
            ShaderVisibility visibility,
            List<RootDeclaration> unboundedTables)
        {
            var declaration = new RootDeclaration(
                RootParameterType.TypeDescriptorTable,
                [new DescriptorRange1(
                    facts.RangeType,
                    uint.MaxValue,
                    facts.ShaderRegister,
                    facts.RegisterSpace,
                    facts.Heap == ParameterHeap.Sampler
                        ? DescriptorRangeFlags.None
                        : DescriptorRangeFlags.DataVolatile,
                    uint.MaxValue)],
                default,
                default,
                facts.Heap,
                true,
                visibility);
            _declarations.Add(declaration);
            unboundedTables.Add(declaration);
        }

        private void AddStaticSampler(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint rangeIndex,
            in NativeDescriptorRangeFacts facts,
            ShaderVisibility visibility)
        {
            VariableReflection declaration = dataLayout.GetBindingRangeLeafVariable(rangeIndex);
            if (declaration == VariableReflection.Null ||
                !_staticSamplers.TryGetValue(declaration, out SamplerDesc sampler))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang sampler range {rangeIndex} on layout '{layout.Name}' " +
                    "does not resolve to the declared static sampler.");
            }
            _nativeStaticSamplers.Add(ToNativeStaticSampler(
                sampler,
                facts.ShaderRegister,
                facts.RegisterSpace,
                visibility));
            _resolvedStaticSamplers.Add(declaration);
        }

        private static void AddBoundedDescriptorRange(
            in NativeDescriptorRangeFacts facts,
            List<DescriptorRange1> resourceRanges,
            List<DescriptorRange1> samplerRanges,
            ref uint resourceCount,
            ref uint samplerCount)
        {
            DescriptorRange1 nativeRange = new(
                facts.RangeType,
                facts.Count,
                facts.ShaderRegister,
                facts.RegisterSpace,
                facts.Heap == ParameterHeap.Sampler
                    ? DescriptorRangeFlags.None
                    : DescriptorRangeFlags.DataVolatile,
                uint.MaxValue);
            if (facts.Heap == ParameterHeap.Resource)
            {
                resourceRanges.Add(nativeRange);
                resourceCount = checked(resourceCount + facts.Count);
            }
            else
            {
                samplerRanges.Add(nativeRange);
                samplerCount = checked(samplerCount + facts.Count);
            }
        }

        private RootDeclaration? AddBoundedTable(
            List<DescriptorRange1> ranges,
            ParameterHeap heap,
            ShaderVisibility visibility)
        {
            if (ranges.Count == 0)
                return null;
            var declaration = new RootDeclaration(
                RootParameterType.TypeDescriptorTable,
                [.. ranges],
                default,
                default,
                heap,
                false,
                visibility);
            _declarations.Add(declaration);
            return declaration;
        }

        private static nuint GetDescriptorSubObjectCategorySpace(
            TypeLayoutReflection layout,
            nint bindingRangeIndex,
            SlangParameterCategory category,
            string layoutName)
        {
            nint subObjectRangeCount = layout.SubObjectRangeCount;
            nuint marker = unchecked((nuint)subObjectRangeCount);
            if (subObjectRangeCount < 0 || marker == Slang.UnknownSize ||
                marker == Slang.UnboundedSize)
                throw new GraphicsException(GraphicsError.NativeFailure,
                    $"Slang layout '{layoutName}' has an unresolved sub-object range count " +
                    "while resolving a descriptor category space.");
            for (nint index = 0; index < subObjectRangeCount; index++)
            {
                if (layout.GetSubObjectRangeBindingRangeIndex(index) != bindingRangeIndex)
                    continue;
                VariableLayoutReflection leaf = layout.GetSubObjectRangeOffset(index);
                if (leaf == VariableLayoutReflection.Null)
                    throw new GraphicsException(GraphicsError.NativeFailure,
                        $"Slang descriptor binding range {bindingRangeIndex} on layout " +
                        $"'{layoutName}' has no variable layout.");
                return leaf.GetBindingSpace(category);
            }
            return 0;
        }

        private void AddChildBlocks(
            VariableLayoutReflection layout,
            ShaderVisibility visibility,
            uint registerSpaceBase)
        {
            TypeLayoutReflection dataLayout = GetParameterDataLayout(layout);
            nint subObjectRangeCount = GetResolvedSubObjectRangeCount(layout, dataLayout);
            nint bindingRangeCount = GetResolvedChildBindingRangeCount(layout, dataLayout);
            for (nint index = 0; index < subObjectRangeCount; index++)
            {
                AddChildBlock(
                    layout,
                    dataLayout,
                    index,
                    bindingRangeCount,
                    visibility,
                    registerSpaceBase);
            }
        }

        private static nint GetResolvedSubObjectRangeCount(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout)
        {
            nint count = dataLayout.SubObjectRangeCount;
            nuint marker = unchecked((nuint)count);
            if (count < 0 || marker == Slang.UnknownSize || marker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang layout '{layout.Name}' has an unresolved sub-object range count.");
            }
            return count;
        }

        private static nint GetResolvedChildBindingRangeCount(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout)
        {
            nint count = dataLayout.BindingRangeCount;
            nuint marker = unchecked((nuint)count);
            if (count < 0 || marker == Slang.UnknownSize || marker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang layout '{layout.Name}' has an unresolved binding-range count while traversing sub-objects.");
            }
            return count;
        }

        private void AddChildBlock(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint index,
            nint bindingRangeCount,
            ShaderVisibility visibility,
            uint registerSpaceBase)
        {
            nint bindingRangeIndex = dataLayout.GetSubObjectRangeBindingRangeIndex(index);
            nuint bindingRangeIndexMarker = unchecked((nuint)bindingRangeIndex);
            if (bindingRangeIndex < 0 || bindingRangeIndexMarker == Slang.UnknownSize ||
                bindingRangeIndexMarker == Slang.UnboundedSize ||
                bindingRangeIndex >= bindingRangeCount)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang sub-object range {index} for layout " +
                    $"'{layout.Name}' has invalid binding-range index {bindingRangeIndex}.");
            }
            SlangBindingType source =
                dataLayout.GetBindingRangeType(bindingRangeIndex) & SlangBindingType.BaseMask;
            nint nativeDescriptorRanges =
                dataLayout.GetBindingRangeDescriptorRangeCount(bindingRangeIndex);
            nuint descriptorRangeMarker = unchecked((nuint)nativeDescriptorRanges);
            if (nativeDescriptorRanges < 0 || descriptorRangeMarker == Slang.UnknownSize ||
                descriptorRangeMarker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang sub-object binding range {bindingRangeIndex} on layout " +
                    $"'{layout.Name}' has an unresolved descriptor-range count.");
            }
            if (nativeDescriptorRanges != 0)
                return;
            ValidateDescriptorlessChildBinding(layout, dataLayout, bindingRangeIndex, source);
            VariableLayoutReflection child = dataLayout.GetSubObjectRangeOffset(index);
            if (child == VariableLayoutReflection.Null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang sub-object range {index} for layout " +
                    $"'{layout.Name}' has no variable layout.");
            }
            uint childRegisterSpaceBase = source == SlangBindingType.ParameterBlock
                ? ResolveChildRegisterSpaceBase(
                    layout,
                    dataLayout,
                    child,
                    index,
                    registerSpaceBase)
                : registerSpaceBase;
            AddBlock(child, visibility, source, childRegisterSpaceBase);
        }

        private static void ValidateDescriptorlessChildBinding(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            nint bindingRangeIndex,
            SlangBindingType source)
        {
            if (source is not (
                SlangBindingType.ParameterBlock or SlangBindingType.ConstantBuffer or
                SlangBindingType.InlineUniformData or SlangBindingType.PushConstant))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang sub-object binding range {bindingRangeIndex} on layout " +
                    $"'{layout.Name}' has unsupported type {source} and no native descriptor " +
                    "range; D3D12 lowering cannot silently omit it.");
            }
            nint occurrenceCount = dataLayout.GetBindingRangeBindingCount(bindingRangeIndex);
            nuint occurrenceMarker = unchecked((nuint)occurrenceCount);
            if (occurrenceCount < 0 || occurrenceMarker == Slang.UnknownSize ||
                occurrenceMarker == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang sub-object binding range {bindingRangeIndex} on layout " +
                    $"'{layout.Name}' has an unresolved occurrence count.");
            }
        }

        private static uint ResolveChildRegisterSpaceBase(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            VariableLayoutReflection child,
            nint index,
            uint registerSpaceBase)
        {
            nint reflectedSpace = dataLayout.GetSubObjectRangeSpaceOffset(index);
            nuint relativeSpace = unchecked((nuint)reflectedSpace);
            if (relativeSpace == Slang.UnknownSize || relativeSpace == Slang.UnboundedSize ||
                reflectedSpace < 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang parameter block sub-object range {index} for layout " +
                    $"'{layout.Name}' has an unresolved register-space offset.");
            }
            nuint variableSpace = child.GetOffset(SlangParameterCategory.SubElementRegisterSpace);
            if (variableSpace == Slang.UnknownSize || variableSpace == Slang.UnboundedSize)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang parameter block sub-object range {index} for layout " +
                    $"'{layout.Name}' has an unresolved variable register-space offset.");
            }
            if (relativeSpace > uint.MaxValue || variableSpace > uint.MaxValue ||
                relativeSpace > uint.MaxValue - variableSpace ||
                relativeSpace + variableSpace > uint.MaxValue - registerSpaceBase)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang parameter block sub-object range {index} for layout " +
                    $"'{layout.Name}' exceeds D3D12 register-space limits.");
            }
            return checked(registerSpaceBase + (uint)(relativeSpace + variableSpace));
        }

        private D3D12RootSignatureState Build()
        {
            RootDeclaration[] declarations = [.. _declarations];
            AssignRootArgumentLocations(declarations);
            StaticSamplerDesc[] staticSamplers = [.. _nativeStaticSamplers];
            ID3D12RootSignature* rootSignature = CreateNativeRootSignature(
                declarations,
                staticSamplers,
                out byte[] serialized);
            try
            {
                Dictionary<VariableLayoutReflection, NativeParameterBinding> blocks =
                    CreateNativeParameterBindings();
                DefaultRootTable[] defaults = CreateDefaultRootTables(declarations);
                uint rootArgumentSize = GetRootArgumentSize(declarations);
                return new D3D12RootSignatureState(
                    rootSignature,
                    _reflection,
                    blocks,
                    defaults,
                    staticSamplers,
                    serialized,
                    rootArgumentSize);
            }
            catch
            {
                _ = rootSignature->Release();
                throw;
            }
        }

        private static void AssignRootArgumentLocations(RootDeclaration[] declarations)
        {
            for (int index = 0; index < declarations.Length; index++)
            {
                RootDeclaration declaration = declarations[index];
                declaration.RootParameterIndex = checked((uint)index);
                uint precedingEnd = index == 0
                    ? 0
                    : checked(
                        declarations[index - 1].RootArgumentOffset +
                        RootArgumentByteSize(declarations[index - 1]));
                uint alignment = declaration.Type == RootParameterType.Type32BitConstants
                    ? (uint)sizeof(uint)
                    : (uint)sizeof(ulong);
                declaration.RootArgumentOffset = checked(
                    (precedingEnd + alignment - 1) & ~(alignment - 1));
            }
        }

        private ID3D12RootSignature* CreateNativeRootSignature(
            RootDeclaration[] declarations,
            StaticSamplerDesc[] staticSamplers,
            out byte[] serialized)
        {
            RootParameter1[] parameters = new RootParameter1[declarations.Length];
            int rangeCount = declarations.Sum(static declaration => declaration.Ranges.Length);
            DescriptorRange1[] ranges = new DescriptorRange1[rangeCount];
            fixed (DescriptorRange1* rangePointer = ranges)
            {
                PopulateRootParameters(declarations, parameters, ranges, rangePointer);
                return SerializeAndCreateRootSignature(parameters, staticSamplers, out serialized);
            }
        }

        private static void PopulateRootParameters(
            RootDeclaration[] declarations,
            RootParameter1[] parameters,
            DescriptorRange1[] ranges,
            DescriptorRange1* rangePointer)
        {
            int rangeOffset = 0;
            for (int index = 0; index < declarations.Length; index++)
            {
                RootDeclaration declaration = declarations[index];
                ShaderVisibility visibility = declaration.Visibility;
                if (declaration.Type == RootParameterType.Type32BitConstants)
                {
                    parameters[index] = new RootParameter1(
                        RootParameterType.Type32BitConstants,
                        shaderVisibility: visibility,
                        constants: declaration.Constants);
                    continue;
                }
                if (declaration.Type == RootParameterType.TypeCbv)
                {
                    parameters[index] = new RootParameter1(
                        RootParameterType.TypeCbv,
                        shaderVisibility: visibility,
                        descriptor: declaration.Descriptor);
                    continue;
                }
                declaration.Ranges.CopyTo(ranges, rangeOffset);
                parameters[index] = new RootParameter1(
                    RootParameterType.TypeDescriptorTable,
                    shaderVisibility: visibility,
                    descriptorTable: new RootDescriptorTable1(
                        checked((uint)declaration.Ranges.Length),
                        rangePointer + rangeOffset));
                rangeOffset += declaration.Ranges.Length;
            }
        }

        private ID3D12RootSignature* SerializeAndCreateRootSignature(
            RootParameter1[] parameters,
            StaticSamplerDesc[] staticSamplers,
            out byte[] serialized)
        {
            ID3D10Blob* serializedBlob = null;
            ID3D10Blob* errorBlob = null;
            ID3D12RootSignature* rootSignature = null;
            fixed (RootParameter1* parameterPointer = parameters)
            fixed (StaticSamplerDesc* staticSamplerPointer = staticSamplers)
            {
                RootSignatureDesc1 description = new(
                    checked((uint)parameters.Length),
                    parameterPointer,
                    checked((uint)staticSamplers.Length),
                    staticSamplers.Length == 0 ? null : staticSamplerPointer,
                    RootFlags());
                VersionedRootSignatureDesc versioned = new(
                    D3DRootSignatureVersion.Version11,
                    desc11: description);
                int result = _backend._d3d12.SerializeVersionedRootSignature(
                    &versioned,
                    &serializedBlob,
                    &errorBlob);
                if (result < 0)
                {
                    string detail = BlobText(errorBlob);
                    ReleaseBlob(serializedBlob);
                    ReleaseBlob(errorBlob);
                    ThrowIfFailed(
                        _device,
                        result,
                        NativeOperationType.PipelineCreation,
                        "D3D12SerializeVersionedRootSignature",
                        detail);
                }
                try
                {
                    serialized = new ReadOnlySpan<byte>(
                        serializedBlob->GetBufferPointer(),
                        checked((int)serializedBlob->GetBufferSize())).ToArray();
                    Guid iid = ID3D12RootSignature.Guid;
                    ThrowIfFailed(
                        _device,
                        _device.Native->CreateRootSignature(
                            _device.EnabledNodeMask,
                            serializedBlob->GetBufferPointer(),
                            serializedBlob->GetBufferSize(),
                            &iid,
                            (void**)&rootSignature),
                        NativeOperationType.PipelineCreation,
                        "ID3D12Device::CreateRootSignature");
                }
                finally
                {
                    ReleaseBlob(serializedBlob);
                    ReleaseBlob(errorBlob);
                }
            }
            return rootSignature;
        }

        private Dictionary<VariableLayoutReflection, NativeParameterBinding>
            CreateNativeParameterBindings()
        {
            Dictionary<VariableLayoutReflection, NativeParameterBinding> blocks = [];
            foreach ((VariableLayoutReflection layout, NativeParameterBindingBuild candidate)
                     in _parameterBindingBuilds)
            {
                D3D12BoundedTable? resourceTable = ToBoundedTable(
                    candidate.ResourceTable,
                    candidate.ResourceDescriptorCount);
                D3D12BoundedTable? samplerTable = ToBoundedTable(
                    candidate.SamplerTable,
                    candidate.SamplerDescriptorCount);
                OrdinaryRootBinding? ordinaryRoot = CreateOrdinaryRootBinding(candidate);
                blocks.Add(
                    layout,
                    new NativeParameterBinding(
                        resourceTable,
                        samplerTable,
                        ordinaryRoot,
                        candidate.RegisterSpaceBase,
                        candidate.Slots));
            }
            return blocks;
        }

        private static D3D12BoundedTable? ToBoundedTable(
            RootDeclaration? declaration,
            uint descriptorCount) =>
            declaration is null
                ? null
                : new D3D12BoundedTable(
                    declaration.RootParameterIndex,
                    declaration.RootArgumentOffset,
                    descriptorCount);

        private static OrdinaryRootBinding? CreateOrdinaryRootBinding(
            NativeParameterBindingBuild candidate)
        {
            if (candidate.Ordinary is not RootDeclaration ordinary)
                return null;
            bool constants = ordinary.Type == RootParameterType.Type32BitConstants;
            return new OrdinaryRootBinding(
                ordinary.RootParameterIndex,
                ordinary.RootArgumentOffset,
                constants,
                constants ? ordinary.Constants.Num32BitValues : 0,
                candidate.OrdinaryDataSize);
        }

        private static DefaultRootTable[] CreateDefaultRootTables(
            RootDeclaration[] declarations) =>
            declarations
                .Where(static declaration => declaration.Unbounded)
                .Select(static declaration => new DefaultRootTable(
                    declaration.RootParameterIndex,
                    declaration.RootArgumentOffset,
                    declaration.Heap))
                .ToArray();

        private uint GetRootArgumentSize(RootDeclaration[] declarations) =>
            _localRootSignature && declarations.Length != 0
                ? checked(
                    declarations[^1].RootArgumentOffset +
                    RootArgumentByteSize(declarations[^1]))
                : 0;

        private static uint RootArgumentByteSize(RootDeclaration declaration) =>
            declaration.Type == RootParameterType.Type32BitConstants
                ? checked(declaration.Constants.Num32BitValues * sizeof(uint))
                : sizeof(ulong);

        private static StaticSamplerDesc ToNativeStaticSampler(
            in SamplerDesc state,
            uint shaderRegister,
            uint registerSpace,
            ShaderVisibility visibility)
        {
            return new StaticSamplerDesc
            {
                Filter = ToFilter(state),
                AddressU = ToAddressMode(state.AddressU),
                AddressV = ToAddressMode(state.AddressV),
                AddressW = ToAddressMode(state.AddressW),
                MipLODBias = state.MipLodBias,
                MaxAnisotropy = state.MaximumAnisotropy,
                ComparisonFunc = state.Comparison is CompareOperation comparison
                    ? ToComparison(comparison)
                    : ComparisonFunc.Always,
                BorderColor = ToStaticBorderColor(state.BorderColor),
                MinLOD = state.MinimumLod,
                MaxLOD = state.MaximumLod,
                ShaderRegister = shaderRegister,
                RegisterSpace = registerSpace,
                ShaderVisibility = visibility,
            };
        }

        private static StaticBorderColor ToStaticBorderColor(
            System.Numerics.Vector4 color) => color switch
            {
                { X: 0, Y: 0, Z: 0, W: 0 } => StaticBorderColor.TransparentBlack,
                { X: 0, Y: 0, Z: 0, W: 1 } => StaticBorderColor.OpaqueBlack,
                { X: 1, Y: 1, Z: 1, W: 1 } => StaticBorderColor.OpaqueWhite,
                _ => throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "A D3D12 static sampler border color must be transparent black, " +
                    "opaque black, or opaque white."),
            };

        private RootSignatureFlags RootFlags()
        {
            if (_localRootSignature)
                return RootSignatureFlags.LocalRootSignature;

            RootSignatureFlags result = RootSignatureFlags.None;
            if (_allowInputAssembler)
                result |= RootSignatureFlags.AllowInputAssemblerInputLayout;
            if (_allowStreamOutput)
                result |= RootSignatureFlags.AllowStreamOutput;

            if (_pipelineType == PipelineType.Compute)
            {
                result |= RootSignatureFlags.DenyVertexShaderRootAccess |
                    RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess |
                    RootSignatureFlags.DenyPixelShaderRootAccess |
                    RootSignatureFlags.DenyAmplificationShaderRootAccess |
                    RootSignatureFlags.DenyMeshShaderRootAccess;
            }
            else if (_pipelineType == PipelineType.Graphics)
            {
                result |= RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess |
                    RootSignatureFlags.DenyAmplificationShaderRootAccess |
                    RootSignatureFlags.DenyMeshShaderRootAccess;
            }
            else if (_pipelineType == PipelineType.Mesh)
            {
                result |= RootSignatureFlags.DenyVertexShaderRootAccess |
                    RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess;
            }
            return result;
        }

        private sealed class NativeParameterBindingBuild
        {
            internal NativeParameterBindingBuild(
                RootDeclaration? resourceTable,
                uint resourceDescriptorCount,
                RootDeclaration? samplerTable,
                uint samplerDescriptorCount,
                RootDeclaration? ordinary,
                uint ordinaryDataSize,
                uint registerSpaceBase,
                DescriptorSlotDesc[] slots,
                int staticSamplerStart,
                int staticSamplerCount,
                RootDeclaration[] declarations)
            {
                ResourceTable = resourceTable;
                ResourceDescriptorCount = resourceDescriptorCount;
                SamplerTable = samplerTable;
                SamplerDescriptorCount = samplerDescriptorCount;
                Ordinary = ordinary;
                OrdinaryDataSize = ordinaryDataSize;
                RegisterSpaceBase = registerSpaceBase;
                Slots = slots;
                StaticSamplerStart = staticSamplerStart;
                StaticSamplerCount = staticSamplerCount;
                Declarations = declarations;
            }

            internal RootDeclaration? ResourceTable { get; }
            internal uint ResourceDescriptorCount { get; }
            internal RootDeclaration? SamplerTable { get; }
            internal uint SamplerDescriptorCount { get; }
            internal RootDeclaration? Ordinary { get; }
            internal uint OrdinaryDataSize { get; }
            internal uint RegisterSpaceBase { get; }
            internal DescriptorSlotDesc[] Slots { get; }
            internal int StaticSamplerStart { get; }
            internal int StaticSamplerCount { get; }
            internal RootDeclaration[] Declarations { get; }
        }
    }

    private static ShaderVisibility ToNativeVisibility(SlangStage stage) => stage switch
    {
        SlangStage.Vertex => ShaderVisibility.Vertex,
        SlangStage.Hull => ShaderVisibility.Hull,
        SlangStage.Domain => ShaderVisibility.Domain,
        SlangStage.Geometry => ShaderVisibility.Geometry,
        SlangStage.Fragment => ShaderVisibility.Pixel,
        SlangStage.Amplification => ShaderVisibility.Amplification,
        SlangStage.Mesh => ShaderVisibility.Mesh,
        _ => ShaderVisibility.All,
    };

    private static ShaderVisibility MergeVisibility(
        ShaderVisibility left,
        ShaderVisibility right) => left == right ? left : ShaderVisibility.All;

    private static string BlobText(ID3D10Blob* blob)
    {
        if (blob is null || blob->GetBufferPointer() is null || blob->GetBufferSize() == 0)
            return string.Empty;
        ReadOnlySpan<byte> bytes = new(
            blob->GetBufferPointer(),
            checked((int)blob->GetBufferSize()));
        int length = bytes.IndexOf((byte)0);
        if (length >= 0)
            bytes = bytes[..length];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void ReleaseBlob(ID3D10Blob* blob)
    {
        if (blob is not null)
            _ = blob->Release();
    }

    internal static StaticSamplerDesc[] GetCompiledStaticSamplers(Pipeline pipeline) =>
        RequireD3D12.Pipeline(pipeline).RootSignature.StaticSamplers.ToArray();

    internal static byte[] GetSerializedRootSignature(Pipeline pipeline) =>
        RequireD3D12.Pipeline(pipeline).RootSignature.Serialized.ToArray();

}
