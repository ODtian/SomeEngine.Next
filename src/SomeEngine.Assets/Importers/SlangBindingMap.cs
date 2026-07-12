using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static partial class SlangBindingMap
{
    private const byte Unknown = 0;
    private const byte ConstantBuffer = 1;
    private const byte TextureSrv = 2;
    private const byte BufferSrv = 3;
    private const byte TextureUav = 4;
    private const byte BufferUav = 5;
    private const byte Sampler = 6;
    private const byte InputAttachment = 7;
    private const byte AccelStruct = 8;

    public readonly record struct ResourceKey(
        string Name,
        uint Binding,
        uint Space,
        byte ResourceType,
        Schema.ShaderBindingType BindingType,
        uint DescriptorCount,
        Schema.ShaderReflectedAccess ReflectedAccess,
        Schema.ShaderReflectedOperations ReflectedOperations,
        Schema.ShaderDeclaredEffect DeclaredEffect,
        Schema.ShaderDeclaredOperations DeclaredOperations,
        Schema.ShaderTextureDimension TextureDimension,
        Schema.ShaderTextureSampleType TextureSampleType,
        Schema.ShaderStorageFormat StorageFormat,
        uint SlangResourceShape,
        uint SlangResourceAccess,
        uint SlangScalarType,
        uint SlangImageFormat);

    private readonly record struct ResourceFacts(
        byte ResourceType,
        Schema.ShaderBindingType BindingType,
        uint DescriptorCount,
        Schema.ShaderReflectedAccess ReflectedAccess,
        Schema.ShaderReflectedOperations ReflectedOperations,
        Schema.ShaderDeclaredEffect DeclaredEffect,
        Schema.ShaderDeclaredOperations DeclaredOperations,
        Schema.ShaderTextureDimension TextureDimension,
        Schema.ShaderTextureSampleType TextureSampleType,
        Schema.ShaderStorageFormat StorageFormat,
        uint SlangResourceShape,
        uint SlangResourceAccess,
        uint SlangScalarType,
        uint SlangImageFormat);

    public readonly record struct SpaceKey(SlangParameterCategory Category, uint Space);

    public static void Fill(
        Dictionary<ResourceKey, uint> resourceMap,
        Schema.ShaderReflectionData dest)
    {
        dest.Resources ??= [];

        foreach (var kvp in resourceMap
            .OrderBy(static kvp => kvp.Key.Space)
            .ThenBy(static kvp => kvp.Key.Binding)
            .ThenBy(static kvp => kvp.Key.ResourceType)
            .ThenBy(static kvp => kvp.Key.BindingType)
            .ThenBy(static kvp => kvp.Key.DescriptorCount)
            .ThenBy(static kvp => kvp.Key.ReflectedAccess)
            .ThenBy(static kvp => kvp.Key.ReflectedOperations)
            .ThenBy(static kvp => kvp.Key.DeclaredEffect)
            .ThenBy(static kvp => kvp.Key.DeclaredOperations)
            .ThenBy(static kvp => kvp.Key.TextureDimension)
            .ThenBy(static kvp => kvp.Key.TextureSampleType)
            .ThenBy(static kvp => kvp.Key.StorageFormat)
            .ThenBy(static kvp => kvp.Key.SlangResourceShape)
            .ThenBy(static kvp => kvp.Key.SlangResourceAccess)
            .ThenBy(static kvp => kvp.Key.SlangScalarType)
            .ThenBy(static kvp => kvp.Key.SlangImageFormat)
            .ThenBy(static kvp => kvp.Key.Name, StringComparer.Ordinal))
        {
            dest.Resources.Add(
                new Schema.ShaderResourceReflection
                {
                    Name = kvp.Key.Name,
                    Stages = kvp.Value,
                    Binding = kvp.Key.Binding,
                    Space = kvp.Key.Space,
                    ResourceType = kvp.Key.ResourceType,
                    BindingType = kvp.Key.BindingType,
                    DescriptorCount = kvp.Key.DescriptorCount,
                    ReflectedAccess = kvp.Key.ReflectedAccess,
                    ReflectedOperations = kvp.Key.ReflectedOperations,
                    DeclaredEffect = kvp.Key.DeclaredEffect,
                    DeclaredOperations = kvp.Key.DeclaredOperations,
                    TextureDimension = kvp.Key.TextureDimension,
                    TextureSampleType = kvp.Key.TextureSampleType,
                    StorageFormat = kvp.Key.StorageFormat,
                    SlangResourceShape = kvp.Key.SlangResourceShape,
                    SlangResourceAccess = kvp.Key.SlangResourceAccess,
                    SlangScalarType = kvp.Key.SlangScalarType,
                    SlangImageFormat = kvp.Key.SlangImageFormat,
                });
        }
    }

    public static Schema.ShaderReflectionData Create(Dictionary<ResourceKey, uint> resourceMap)
    {
        var reflectionData = new Schema.ShaderReflectionData { Resources = [] };
        Fill(resourceMap, reflectionData);
        return reflectionData;
    }

    public static void Merge(
        Dictionary<ResourceKey, uint> destination,
        Dictionary<ResourceKey, uint> source)
    {
        foreach (var kvp in source)
        {
            destination[kvp.Key] = destination.GetValueOrDefault(kvp.Key) | kvp.Value;
        }
    }

    public static Dictionary<SpaceKey, uint> Next(
        Dictionary<ResourceKey, uint> resources,
        string backend)
    {
        var result = new Dictionary<SpaceKey, uint>();
        foreach (ResourceKey resource in resources.Keys)
        {
            var key = new SpaceKey(BindSpace(backend, resource.ResourceType), resource.Space);
            uint nextBinding = checked(resource.Binding + Math.Max(1U, resource.DescriptorCount));
            if (!result.TryGetValue(key, out uint existing) || nextBinding > existing)
            {
                result[key] = nextBinding;
            }
        }

        return result;
    }

    public static void Collect(
        VariableLayoutReflection varLayout,
        Schema.ShaderStage stage,
        Dictionary<ResourceKey, uint> resources,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases = null,
        HashSet<string>? skipNames = null)
    {
        Visit(varLayout, 0, 0, false, stage, resources, backend, entryBases, skipNames);
    }

    private static void Visit(
        VariableLayoutReflection layout,
        uint baseBinding,
        uint baseSpace,
        bool fieldBinding,
        Schema.ShaderStage stage,
        Dictionary<ResourceKey, uint> resources,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases,
        HashSet<string>? skipNames)
    {
        if (layout == VariableLayoutReflection.Null)
        {
            return;
        }

        ValidateResourceEffectTarget(layout);
        if (TryAdd(layout, baseBinding, baseSpace, fieldBinding, stage, resources, backend, entryBases, skipNames))
        {
            return;
        }

        TypeLayoutReflection typeLayout = layout.TypeLayout.UnwrapArray();
        if (typeLayout == TypeLayoutReflection.Null)
        {
            return;
        }

        VisitFields(layout, typeLayout, baseBinding, baseSpace, fieldBinding, stage, resources, backend, entryBases, skipNames);
    }

    private static void VisitFields(
        VariableLayoutReflection layout,
        TypeLayoutReflection typeLayout,
        uint baseBinding,
        uint baseSpace,
        bool fieldBinding,
        Schema.ShaderStage stage,
        Dictionary<ResourceKey, uint> resources,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases,
        HashSet<string>? skipNames)
    {
        uint childBaseBinding = fieldBinding ? baseBinding : layout.BindingIndex;
        uint childBaseSpace = fieldBinding
            ? baseSpace
            : GetSpace(layout, SlangParameterCategory.DescriptorTableSlot, 0);
        for (uint i = 0; i < typeLayout.FieldCount; i++)
        {
            Visit(typeLayout.GetFieldByIndex(i), childBaseBinding, childBaseSpace, true, stage, resources, backend, entryBases, skipNames);
        }
    }

    private static bool TryAdd(
        VariableLayoutReflection layout,
        uint baseBinding,
        uint baseSpace,
        bool fieldBinding,
        Schema.ShaderStage stage,
        Dictionary<ResourceKey, uint> resources,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases,
        HashSet<string>? skipNames)
    {
        var resourceInfo = ResourceInfo(layout, out _);
        if (resourceInfo.ResourceType == Unknown || string.IsNullOrEmpty(layout.Name))
        {
            return resourceInfo.ResourceType != Unknown;
        }

        if (skipNames?.Contains(layout.Name) == true)
        {
            return true;
        }

        ResourceKey key = CreateResourceKey(layout, baseBinding, baseSpace, fieldBinding, backend, entryBases, resourceInfo);
        resources[key] = resources.GetValueOrDefault(key) | StageFlags(stage);
        return true;
    }

    private static ResourceKey CreateResourceKey(
        VariableLayoutReflection layout,
        uint baseBinding,
        uint baseSpace,
        bool fieldBinding,
        string backend,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases,
        ResourceFacts resourceInfo)
    {
        SlangParameterCategory bindingNamespace = BindSpace(backend, layout, resourceInfo.ResourceType);
        uint space = GetSpace(layout, bindingNamespace, baseSpace);
        uint relativeBinding = fieldBinding ? baseBinding + layout.BindingIndex : layout.BindingIndex;
        uint binding = BindingWithEntryBase(bindingNamespace, space, relativeBinding, entryBases);
        return new ResourceKey(
            layout.Name,
            binding,
            space,
            resourceInfo.ResourceType,
            resourceInfo.BindingType,
            resourceInfo.DescriptorCount,
            resourceInfo.ReflectedAccess,
            resourceInfo.ReflectedOperations,
            resourceInfo.DeclaredEffect,
            resourceInfo.DeclaredOperations,
            resourceInfo.TextureDimension,
            resourceInfo.TextureSampleType,
            resourceInfo.StorageFormat,
            resourceInfo.SlangResourceShape,
            resourceInfo.SlangResourceAccess,
            resourceInfo.SlangScalarType,
            resourceInfo.SlangImageFormat);
    }

    private static uint BindingWithEntryBase(
        SlangParameterCategory bindingNamespace,
        uint space,
        uint relativeBinding,
        IReadOnlyDictionary<SpaceKey, uint>? entryBases)
    {
        if (entryBases == null)
        {
            return relativeBinding;
        }

        var namespaceKey = new SpaceKey(bindingNamespace, space);
        entryBases.TryGetValue(namespaceKey, out uint baseEntryPointBinding);
        return baseEntryPointBinding + relativeBinding;
    }

    private static SlangParameterCategory BindSpace(
        string backend,
        VariableLayoutReflection layout,
        byte resourceType)
    {
        return layout.Category == SlangParameterCategory.DescriptorTableSlot
            ? SlangParameterCategory.DescriptorTableSlot
            : BindSpace(backend, resourceType);
    }

    private static SlangParameterCategory BindSpace(
        string backend,
        byte resourceType)
    {
        if (string.Equals(backend, "spirv", StringComparison.Ordinal))
        {
            return SlangParameterCategory.DescriptorTableSlot;
        }

        return resourceType switch
        {
            ConstantBuffer => SlangParameterCategory.ConstantBuffer,
            TextureSrv or BufferSrv or InputAttachment or AccelStruct => SlangParameterCategory.ShaderResource,
            TextureUav or BufferUav => SlangParameterCategory.UnorderedAccess,
            Sampler => SlangParameterCategory.SamplerState,
            _ => SlangParameterCategory.None,
        };
    }

    private static ResourceFacts ResourceInfo(
        VariableLayoutReflection layout,
        out SlangParameterCategory category)
    {
        category = Normalize(layout.Category);
        TypeReflection declaredType = layout.Type;
        TypeReflection type = declaredType.UnwrapArray();
        SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;

        (byte ResourceType, Schema.ShaderBindingType BindingType) reflectedResource = category switch
        {
            SlangParameterCategory.ConstantBuffer
            or SlangParameterCategory.PushConstantBuffer =>
                (ConstantBuffer, Schema.ShaderBindingType.ConstantBuffer),
            SlangParameterCategory.SamplerState =>
                (Sampler, Schema.ShaderBindingType.Sampler),
            SlangParameterCategory.Subpass =>
                (InputAttachment, Schema.ShaderBindingType.TextureRead),
            SlangParameterCategory.ShaderResource => ReadOnly(shape),
            SlangParameterCategory.UnorderedAccess => ReadWrite(shape),
            _ => (Unknown, Schema.ShaderBindingType.None),
        };

        if (reflectedResource.ResourceType == Unknown)
        {
            switch (type.Kind)
            {
                case SlangTypeKind.ConstantBuffer:
                    category = SlangParameterCategory.ConstantBuffer;
                    reflectedResource = (ConstantBuffer, Schema.ShaderBindingType.ConstantBuffer);
                    break;
                case SlangTypeKind.SamplerState:
                    category = SlangParameterCategory.SamplerState;
                    reflectedResource = (Sampler, Schema.ShaderBindingType.Sampler);
                    break;
                case SlangTypeKind.Resource:
                case SlangTypeKind.TextureBuffer:
                case SlangTypeKind.ShaderStorageBuffer:
                    bool isWrite = IsWrite(type.ResourceAccess);
                    category = isWrite
                        ? SlangParameterCategory.UnorderedAccess
                        : SlangParameterCategory.ShaderResource;
                    reflectedResource = isWrite ? ReadWrite(shape) : ReadOnly(shape);
                    break;
            }
        }

        return Describe(layout, declaredType, type, reflectedResource);
    }

    private static ResourceFacts Describe(
        VariableLayoutReflection layout,
        TypeReflection declaredType,
        TypeReflection type,
        (byte ResourceType, Schema.ShaderBindingType BindingType) resource)
    {
        SlangResourceShape shape = type.ResourceShape;
        SlangResourceAccess access = type.ResourceAccess;
        SlangScalarType scalarType = ResourceScalarType(type);
        SlangImageFormat imageFormat = layout.ImageFormat;
        Schema.ShaderReflectedAccess reflectedAccess = ReflectedAccess(access, resource.BindingType);
        (Schema.ShaderDeclaredEffect Effect, Schema.ShaderDeclaredOperations Operations) declared =
            DeclaredEffect(layout.Variable, reflectedAccess, resource.BindingType);

        bool texture = resource.BindingType is
            Schema.ShaderBindingType.TextureRead or
            Schema.ShaderBindingType.TextureReadWrite;
        return new ResourceFacts(
            resource.ResourceType,
            resource.BindingType,
            DescriptorCount(declaredType),
            reflectedAccess,
            ReflectedOperations(access),
            declared.Effect,
            declared.Operations,
            texture ? TextureDimension(shape) : Schema.ShaderTextureDimension.Unknown,
            texture ? TextureSampleType(shape, scalarType) : Schema.ShaderTextureSampleType.Unknown,
            resource.BindingType == Schema.ShaderBindingType.TextureReadWrite
                ? StorageFormat(imageFormat)
                : Schema.ShaderStorageFormat.Unknown,
            checked((uint)shape),
            checked((uint)access),
            checked((uint)scalarType),
            checked((uint)imageFormat));
    }

    private static uint DescriptorCount(TypeReflection type)
    {
        if (!type.IsArray) return 1;
        nuint count = type.TotalArrayElementCount;
        return count == Slang.UnknownSize || count == Slang.UnboundedSize || count > uint.MaxValue
            ? 0
            : checked((uint)count);
    }

    private static SlangScalarType ResourceScalarType(TypeReflection type)
    {
        TypeReflection result = type.ResourceResultType;
        if (result == TypeReflection.Null) return SlangScalarType.None;
        if (result.ScalarType != SlangScalarType.None) return result.ScalarType;
        return result.ElementType == TypeReflection.Null
            ? SlangScalarType.None
            : result.ElementType.ScalarType;
    }

    private static Schema.ShaderReflectedAccess ReflectedAccess(
        SlangResourceAccess access,
        Schema.ShaderBindingType bindingType) => access switch
    {
        SlangResourceAccess.Read => Schema.ShaderReflectedAccess.ReadOnly,
        SlangResourceAccess.Write => Schema.ShaderReflectedAccess.WriteOnly,
        SlangResourceAccess.ReadWrite or SlangResourceAccess.RasterOrdered or
            SlangResourceAccess.Append or SlangResourceAccess.Consume or
            SlangResourceAccess.Feedback => Schema.ShaderReflectedAccess.ReadWrite,
        _ when bindingType is Schema.ShaderBindingType.ConstantBuffer or
                              Schema.ShaderBindingType.StorageBufferRead or
                              Schema.ShaderBindingType.RawBufferRead or
                              Schema.ShaderBindingType.TextureRead or
                              Schema.ShaderBindingType.Sampler => Schema.ShaderReflectedAccess.ReadOnly,
        _ => Schema.ShaderReflectedAccess.Unknown,
    };

    private static Schema.ShaderReflectedOperations ReflectedOperations(
        SlangResourceAccess access) => access switch
    {
        SlangResourceAccess.Append =>
            Schema.ShaderReflectedOperations.Atomic | Schema.ShaderReflectedOperations.Append,
        SlangResourceAccess.Consume =>
            Schema.ShaderReflectedOperations.Atomic | Schema.ShaderReflectedOperations.Consume,
        SlangResourceAccess.RasterOrdered => Schema.ShaderReflectedOperations.RasterOrdered,
        SlangResourceAccess.Feedback => Schema.ShaderReflectedOperations.Feedback,
        _ => Schema.ShaderReflectedOperations.None,
    };

    private static Schema.ShaderTextureDimension TextureDimension(SlangResourceShape shape)
    {
        SlangResourceShape supportedFlags = SlangResourceShape.TextureShadowFlag |
            SlangResourceShape.TextureArrayFlag |
            SlangResourceShape.TextureMultisampleFlag;
        if ((shape & SlangResourceShape.ResourceExtShapeMask & ~supportedFlags) != 0)
            return Schema.ShaderTextureDimension.Unknown;

        SlangResourceShape baseShape = shape & SlangResourceShape.BaseShapeMask;
        bool array = (shape & SlangResourceShape.TextureArrayFlag) != 0;
        bool multisample = (shape & SlangResourceShape.TextureMultisampleFlag) != 0;
        return (baseShape, array, multisample) switch
        {
            (SlangResourceShape.Texture1D, false, false) => Schema.ShaderTextureDimension.Texture1D,
            (SlangResourceShape.Texture1D, true, false) => Schema.ShaderTextureDimension.Texture1DArray,
            (SlangResourceShape.Texture2D, false, false) => Schema.ShaderTextureDimension.Texture2D,
            (SlangResourceShape.Texture2D, true, false) => Schema.ShaderTextureDimension.Texture2DArray,
            (SlangResourceShape.Texture2D, false, true) => Schema.ShaderTextureDimension.Texture2DMS,
            (SlangResourceShape.Texture2D, true, true) => Schema.ShaderTextureDimension.Texture2DMSArray,
            (SlangResourceShape.TextureCube, false, false) => Schema.ShaderTextureDimension.Cube,
            (SlangResourceShape.TextureCube, true, false) => Schema.ShaderTextureDimension.CubeArray,
            (SlangResourceShape.Texture3D, false, false) => Schema.ShaderTextureDimension.Texture3D,
            (SlangResourceShape.TextureSubpass, false, false) => Schema.ShaderTextureDimension.Texture2D,
            (SlangResourceShape.TextureSubpass, false, true) => Schema.ShaderTextureDimension.Texture2DMS,
            _ => Schema.ShaderTextureDimension.Unknown,
        };
    }

}

internal static partial class SlangBindingMap
{
    private static (Schema.ShaderDeclaredEffect Effect, Schema.ShaderDeclaredOperations Operations) DeclaredEffect(
        VariableReflection variable,
        Schema.ShaderReflectedAccess reflectedAccess,
        Schema.ShaderBindingType bindingType)
    {
        AttributeReflection selected = FindResourceEffect(variable);
        if (selected == AttributeReflection.Null)
            return (Schema.ShaderDeclaredEffect.Unspecified, Schema.ShaderDeclaredOperations.None);
        if (bindingType == Schema.ShaderBindingType.Sampler)
            throw new InvalidDataException(
                $"Shader sampler '{variable.Name}' cannot declare ResourceEffect.");
        if (selected.ArgumentCount != 2)
            throw new InvalidDataException(
                $"Shader resource '{variable.Name}' ResourceEffect must provide effects and operations arguments.");

        Schema.ShaderDeclaredEffect declared = ParseDeclaredEffect(variable.Name, selected);
        Schema.ShaderDeclaredOperations operations = ParseDeclaredOperations(variable.Name, selected);

        int capability = ReflectedCapability(reflectedAccess, bindingType);
        if (((int)declared & ~capability) != 0)
            throw new InvalidDataException(
                $"Shader resource '{variable.Name}' declares effect {declared} beyond its reflected access {reflectedAccess}.");
        ValidateDeclaredOperations(variable.Name, declared, operations, bindingType, reflectedAccess);
        return (declared, operations);
    }

    private static AttributeReflection FindResourceEffect(VariableReflection variable)
    {
        AttributeReflection selected = AttributeReflection.Null;
        for (uint index = 0; index < variable.AttributeCount; index++)
        {
            AttributeReflection attribute = variable.GetAttribute(index);
            if (attribute == AttributeReflection.Null || !IsResourceEffectAttribute(attribute.Name))
                continue;
            if (selected != AttributeReflection.Null)
                throw new InvalidDataException(
                    $"Shader resource '{variable.Name}' declares ResourceEffect more than once.");
            selected = attribute;
        }
        return selected;
    }

    private static Schema.ShaderDeclaredEffect ParseDeclaredEffect(
        string resourceName,
        AttributeReflection attribute) => attribute.GetArgumentValueInt(0) switch
    {
        1 => Schema.ShaderDeclaredEffect.Read,
        2 => Schema.ShaderDeclaredEffect.Write,
        3 => Schema.ShaderDeclaredEffect.ReadWrite,
        int value => throw new InvalidDataException(
            $"Shader resource '{resourceName}' declares invalid ResourceEffects value {value}."),
    };

    private static Schema.ShaderDeclaredOperations ParseDeclaredOperations(
        string resourceName,
        AttributeReflection attribute)
    {
        int operationValue = attribute.GetArgumentValueInt(1);
        const int allOperations = (int)(
            Schema.ShaderDeclaredOperations.Atomic |
            Schema.ShaderDeclaredOperations.Append |
            Schema.ShaderDeclaredOperations.Consume |
            Schema.ShaderDeclaredOperations.RasterOrdered |
            Schema.ShaderDeclaredOperations.Feedback);
        if (operationValue < 0 || (operationValue & ~allOperations) != 0)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' declares invalid ResourceOperations value {operationValue}.");
        return (Schema.ShaderDeclaredOperations)operationValue;
    }

    private static int ReflectedCapability(
        Schema.ShaderReflectedAccess reflectedAccess,
        Schema.ShaderBindingType bindingType) => reflectedAccess switch
    {
        Schema.ShaderReflectedAccess.ReadOnly => 1,
        Schema.ShaderReflectedAccess.WriteOnly => 2,
        Schema.ShaderReflectedAccess.ReadWrite => 3,
        _ => bindingType switch
        {
            Schema.ShaderBindingType.ConstantBuffer or
                Schema.ShaderBindingType.StorageBufferRead or
                Schema.ShaderBindingType.RawBufferRead or
                Schema.ShaderBindingType.TextureRead => 1,
            Schema.ShaderBindingType.StorageBufferReadWrite or
                Schema.ShaderBindingType.RawBufferReadWrite or
                Schema.ShaderBindingType.TextureReadWrite => 3,
            _ => 0,
        },
    };

    private static void ValidateDeclaredOperations(
        string resourceName,
        Schema.ShaderDeclaredEffect effect,
        Schema.ShaderDeclaredOperations operations,
        Schema.ShaderBindingType bindingType,
        Schema.ShaderReflectedAccess reflectedAccess)
    {
        ValidateStorageOperations(resourceName, operations, bindingType);
        ValidateAtomicOperation(resourceName, effect, operations, reflectedAccess);
        ValidateWriteOperation(
            resourceName,
            effect,
            operations,
            Schema.ShaderDeclaredOperations.Append,
            "Append");
        ValidateReadOperation(resourceName, effect, operations);
        ValidateWriteOperation(
            resourceName,
            effect,
            operations,
            Schema.ShaderDeclaredOperations.RasterOrdered,
            "RasterOrdered");
        ValidateFeedbackOperation(resourceName, effect, operations);
    }

    private static void ValidateStorageOperations(
        string resourceName,
        Schema.ShaderDeclaredOperations operations,
        Schema.ShaderBindingType bindingType)
    {
        bool storage = bindingType is
            Schema.ShaderBindingType.StorageBufferReadWrite or
            Schema.ShaderBindingType.RawBufferReadWrite or
            Schema.ShaderBindingType.TextureReadWrite;
        if (operations != Schema.ShaderDeclaredOperations.None && !storage)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' declares operation qualifiers on non-storage binding {bindingType}.");
    }

    private static void ValidateAtomicOperation(
        string resourceName,
        Schema.ShaderDeclaredEffect effect,
        Schema.ShaderDeclaredOperations operations,
        Schema.ShaderReflectedAccess reflectedAccess)
    {
        if ((operations & Schema.ShaderDeclaredOperations.Atomic) != 0 &&
            (effect != Schema.ShaderDeclaredEffect.ReadWrite ||
             reflectedAccess != Schema.ShaderReflectedAccess.ReadWrite))
        {
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must declare ReadWrite and expose read-write access for Atomic operations.");
        }
    }

    private static void ValidateWriteOperation(
        string resourceName,
        Schema.ShaderDeclaredEffect effect,
        Schema.ShaderDeclaredOperations operations,
        Schema.ShaderDeclaredOperations operation,
        string operationName)
    {
        if ((operations & operation) != 0 &&
            effect is not (Schema.ShaderDeclaredEffect.Write or Schema.ShaderDeclaredEffect.ReadWrite))
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must include Write for {operationName} operations.");
    }

    private static void ValidateReadOperation(
        string resourceName,
        Schema.ShaderDeclaredEffect effect,
        Schema.ShaderDeclaredOperations operations)
    {
        if ((operations & Schema.ShaderDeclaredOperations.Consume) != 0 &&
            effect is not (Schema.ShaderDeclaredEffect.Read or Schema.ShaderDeclaredEffect.ReadWrite))
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must include Read for Consume operations.");
    }

    private static void ValidateFeedbackOperation(
        string resourceName,
        Schema.ShaderDeclaredEffect effect,
        Schema.ShaderDeclaredOperations operations)
    {
        if ((operations & Schema.ShaderDeclaredOperations.Feedback) != 0 &&
            effect != Schema.ShaderDeclaredEffect.ReadWrite)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must declare ReadWrite for Feedback operations.");
    }

    private static bool IsResourceEffectAttribute(string name) =>
        string.Equals(name, "ResourceEffect", StringComparison.Ordinal) ||
        string.Equals(name, "ResourceEffectAttribute", StringComparison.Ordinal) ||
        name.EndsWith("::ResourceEffect", StringComparison.Ordinal) ||
        name.EndsWith("::ResourceEffectAttribute", StringComparison.Ordinal);

    private static void ValidateResourceEffectTarget(VariableLayoutReflection layout)
    {
        VariableReflection variable = layout.Variable;
        bool declared = false;
        for (uint index = 0; index < variable.AttributeCount; index++)
        {
            AttributeReflection attribute = variable.GetAttribute(index);
            if (attribute != AttributeReflection.Null && IsResourceEffectAttribute(attribute.Name))
            {
                declared = true;
                break;
            }
        }
        if (!declared) return;

        TypeReflection type = variable.Type.UnwrapArray();
        if (type.Kind is SlangTypeKind.ParameterBlock or SlangTypeKind.Struct)
            throw new InvalidDataException(
                $"Shader variable '{variable.Name}' declares ResourceEffect on a resource container instead of a resource-valued leaf.");
    }

    private static Schema.ShaderTextureSampleType TextureSampleType(
        SlangResourceShape shape,
        SlangScalarType scalarType)
    {
        if ((shape & SlangResourceShape.TextureShadowFlag) != 0)
            return Schema.ShaderTextureSampleType.Depth;
        return scalarType switch
        {
            SlangScalarType.Float16 or SlangScalarType.Float32 or SlangScalarType.Float64 =>
                Schema.ShaderTextureSampleType.Float,
            SlangScalarType.UInt8 or SlangScalarType.UInt16 or SlangScalarType.UInt32 or
            SlangScalarType.UInt64 or SlangScalarType.UIntPtr => Schema.ShaderTextureSampleType.UInt,
            SlangScalarType.Int8 or SlangScalarType.Int16 or SlangScalarType.Int32 or
            SlangScalarType.Int64 or SlangScalarType.IntPtr => Schema.ShaderTextureSampleType.SInt,
            _ => Schema.ShaderTextureSampleType.Unknown,
        };
    }

    private static Schema.ShaderStorageFormat StorageFormat(SlangImageFormat format) => format switch
    {
        SlangImageFormat.RGBA32f => Schema.ShaderStorageFormat.R32G32B32A32Float,
        SlangImageFormat.RGBA16f => Schema.ShaderStorageFormat.R16G16B16A16Float,
        SlangImageFormat.RG32f => Schema.ShaderStorageFormat.R32G32Float,
        SlangImageFormat.RG16f => Schema.ShaderStorageFormat.R16G16Float,
        SlangImageFormat.R32f => Schema.ShaderStorageFormat.R32Float,
        SlangImageFormat.R16f => Schema.ShaderStorageFormat.R16Float,
        SlangImageFormat.RGBA8 => Schema.ShaderStorageFormat.R8G8B8A8UNorm,
        SlangImageFormat.RG8 => Schema.ShaderStorageFormat.R8G8UNorm,
        SlangImageFormat.R8 => Schema.ShaderStorageFormat.R8UNorm,
        SlangImageFormat.R32ui => Schema.ShaderStorageFormat.R32UInt,
        SlangImageFormat.R16ui => Schema.ShaderStorageFormat.R16UInt,
        _ => Schema.ShaderStorageFormat.Unknown,
    };

    private static SlangParameterCategory Normalize(SlangParameterCategory category)
        => category == SlangParameterCategory.DescriptorTableSlot
            ? SlangParameterCategory.Uniform
            : category;

    private static uint GetSpace(
        VariableLayoutReflection layout,
        SlangParameterCategory category,
        uint fallback)
    {
        uint space = checked((uint)layout.GetBindingSpace(category));
        if (space == 0 && layout.BindingSpace != 0)
        {
            space = layout.BindingSpace;
        }

        return space == 0 ? fallback : space;
    }

    private static bool IsWrite(SlangResourceAccess access)
        => access is SlangResourceAccess.ReadWrite
            or SlangResourceAccess.RasterOrdered
            or SlangResourceAccess.Append
            or SlangResourceAccess.Consume
            or SlangResourceAccess.Feedback
            or SlangResourceAccess.Write;

    private static (byte ResourceType, Schema.ShaderBindingType BindingType) ReadOnly(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferSrv, Schema.ShaderBindingType.RawBufferRead),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferSrv, Schema.ShaderBindingType.StorageBufferRead),
            SlangResourceShape.AccelerationStructure =>
                (AccelStruct, Schema.ShaderBindingType.AccelerationStructure),
            SlangResourceShape.TextureSubpass =>
                (InputAttachment, Schema.ShaderBindingType.TextureRead),
            _ => (TextureSrv, Schema.ShaderBindingType.TextureRead),
        };

    private static (byte ResourceType, Schema.ShaderBindingType BindingType) ReadWrite(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferUav, Schema.ShaderBindingType.RawBufferReadWrite),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferUav, Schema.ShaderBindingType.StorageBufferReadWrite),
            _ => (TextureUav, Schema.ShaderBindingType.TextureReadWrite),
        };

    private static uint StageFlags(Schema.ShaderStage stage)
        => stage switch
        {
            Schema.ShaderStage.Vertex => 0x01,
            Schema.ShaderStage.Pixel => 0x02,
            Schema.ShaderStage.Geometry => 0x04,
            Schema.ShaderStage.Hull => 0x08,
            Schema.ShaderStage.Domain => 0x10,
            Schema.ShaderStage.Compute => 0x20,
            Schema.ShaderStage.Amplification => 0x40,
            Schema.ShaderStage.Mesh => 0x80,
            Schema.ShaderStage.RayGen => 0x100,
            Schema.ShaderStage.RayMiss => 0x200,
            Schema.ShaderStage.RayClosestHit => 0x400,
            Schema.ShaderStage.RayAnyHit => 0x800,
            Schema.ShaderStage.RayIntersection => 0x1000,
            Schema.ShaderStage.Callable => 0x2000,
            _ => 0,
        };
}

