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
        Schema.DescriptorType Kind,
        uint DescriptorCount,
        Schema.AccessEffect Effect,
        Schema.ShaderQualifiers Qualifiers,
        Schema.TextureViewDimension? TextureDimension,
        Schema.TextureSampleType? TextureSampleType,
        Schema.StorageFormat? StorageFormat,
        uint SlangResourceShape,
        uint SlangScalarType,
        uint SlangImageFormat);

    private readonly record struct ResourceFacts(
        byte ResourceType,
        Schema.DescriptorType Kind,
        uint DescriptorCount,
        Schema.AccessEffect Effect,
        Schema.ShaderQualifiers Qualifiers,
        Schema.TextureViewDimension? TextureDimension,
        Schema.TextureSampleType? TextureSampleType,
        Schema.StorageFormat? StorageFormat,
        uint SlangResourceShape,
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
            .ThenBy(static kvp => kvp.Key.Kind)
            .ThenBy(static kvp => kvp.Key.DescriptorCount)
            .ThenBy(static kvp => kvp.Key.Effect)
            .ThenBy(static kvp => kvp.Key.Qualifiers)
            .ThenBy(static kvp => kvp.Key.TextureDimension)
            .ThenBy(static kvp => kvp.Key.TextureSampleType)
            .ThenBy(static kvp => kvp.Key.StorageFormat)
            .ThenBy(static kvp => kvp.Key.SlangResourceShape)
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
                    Kind = kvp.Key.Kind,
                    DescriptorCount = kvp.Key.DescriptorCount,
                    Effect = kvp.Key.Effect,
                    Qualifiers = kvp.Key.Qualifiers,
                    TextureDimension = kvp.Key.TextureDimension,
                    TextureSampleType = kvp.Key.TextureSampleType,
                    StorageFormat = kvp.Key.StorageFormat,
                    SlangResourceShape = kvp.Key.SlangResourceShape,
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

        ValidateAccessEffectTarget(layout);
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
            resourceInfo.Kind,
            resourceInfo.DescriptorCount,
            resourceInfo.Effect,
            resourceInfo.Qualifiers,
            resourceInfo.TextureDimension,
            resourceInfo.TextureSampleType,
            resourceInfo.StorageFormat,
            resourceInfo.SlangResourceShape,
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

        (byte ResourceType, Schema.DescriptorType Kind) reflectedResource = category switch
        {
            SlangParameterCategory.ConstantBuffer
            or SlangParameterCategory.PushConstantBuffer =>
                (ConstantBuffer, Schema.DescriptorType.ConstantBuffer),
            SlangParameterCategory.SamplerState =>
                (Sampler, Schema.DescriptorType.Sampler),
            SlangParameterCategory.Subpass =>
                (InputAttachment, Schema.DescriptorType.SampledTexture),
            SlangParameterCategory.ShaderResource => ReadOnly(shape),
            SlangParameterCategory.UnorderedAccess => ReadWrite(shape),
            _ => (Unknown, Schema.DescriptorType.None),
        };

        if (reflectedResource.ResourceType == Unknown)
        {
            switch (type.Kind)
            {
                case SlangTypeKind.ConstantBuffer:
                    category = SlangParameterCategory.ConstantBuffer;
                    reflectedResource = (ConstantBuffer, Schema.DescriptorType.ConstantBuffer);
                    break;
                case SlangTypeKind.SamplerState:
                    category = SlangParameterCategory.SamplerState;
                    reflectedResource = (Sampler, Schema.DescriptorType.Sampler);
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
        (byte ResourceType, Schema.DescriptorType Kind) resource)
    {
        SlangResourceShape shape = type.ResourceShape;
        SlangResourceAccess access = type.ResourceAccess;
        SlangScalarType scalarType = ResourceScalarType(type);
        SlangImageFormat imageFormat = layout.ImageFormat;
        Schema.AccessEffect reflectedEffect = ReflectedEffect(access, resource.Kind);
        Schema.ShaderQualifiers reflectedQualifiers = ReflectedQualifiers(access);
        (Schema.AccessEffect Effect, Schema.ShaderQualifiers Qualifiers) shaderSlot =
            resource.ResourceType == Unknown
                ? (Schema.AccessEffect.None, Schema.ShaderQualifiers.None)
                : ResolveEffect(layout.Variable, reflectedEffect, reflectedQualifiers, resource.Kind);

        bool texture = resource.Kind is
            Schema.DescriptorType.SampledTexture or
            Schema.DescriptorType.StorageTexture;
        return new ResourceFacts(
            resource.ResourceType,
            resource.Kind,
            DescriptorCount(declaredType),
            shaderSlot.Effect,
            shaderSlot.Qualifiers,
            texture ? TextureDimension(shape) : null,
            texture ? TextureSampleType(shape, scalarType) : null,
            resource.Kind == Schema.DescriptorType.StorageTexture
                ? StorageFormat(imageFormat)
                : null,
            checked((uint)shape),
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

    private static Schema.AccessEffect ReflectedEffect(
        SlangResourceAccess access,
        Schema.DescriptorType kind) => access switch
    {
        SlangResourceAccess.Read => Schema.AccessEffect.Read,
        SlangResourceAccess.Write => Schema.AccessEffect.Write,
        SlangResourceAccess.ReadWrite or SlangResourceAccess.RasterOrdered or
            SlangResourceAccess.Append or SlangResourceAccess.Consume or
            SlangResourceAccess.Feedback => Schema.AccessEffect.ReadWrite,
        _ when kind is Schema.DescriptorType.ConstantBuffer or
                           Schema.DescriptorType.ReadOnlyBuffer or
                           Schema.DescriptorType.SampledTexture or
                           Schema.DescriptorType.Sampler or
                           Schema.DescriptorType.AccelerationStructure => Schema.AccessEffect.Read,
        _ when kind is Schema.DescriptorType.StorageBuffer or
                           Schema.DescriptorType.StorageTexture => Schema.AccessEffect.ReadWrite,
        _ => Schema.AccessEffect.None,
    };

    private static Schema.ShaderQualifiers ReflectedQualifiers(
        SlangResourceAccess access) => access switch
    {
        SlangResourceAccess.Append =>
            Schema.ShaderQualifiers.Atomic | Schema.ShaderQualifiers.Append,
        SlangResourceAccess.Consume =>
            Schema.ShaderQualifiers.Atomic | Schema.ShaderQualifiers.Consume,
        SlangResourceAccess.RasterOrdered => Schema.ShaderQualifiers.RasterOrdered,
        SlangResourceAccess.Feedback => Schema.ShaderQualifiers.Feedback,
        _ => Schema.ShaderQualifiers.None,
    };

    private static Schema.TextureViewDimension? TextureDimension(SlangResourceShape shape)
    {
        SlangResourceShape supportedFlags = SlangResourceShape.TextureShadowFlag |
            SlangResourceShape.TextureArrayFlag |
            SlangResourceShape.TextureMultisampleFlag;
        if ((shape & SlangResourceShape.ResourceExtShapeMask & ~supportedFlags) != 0)
            return null;

        SlangResourceShape baseShape = shape & SlangResourceShape.BaseShapeMask;
        bool array = (shape & SlangResourceShape.TextureArrayFlag) != 0;
        bool multisample = (shape & SlangResourceShape.TextureMultisampleFlag) != 0;
        return (baseShape, array, multisample) switch
        {
            (SlangResourceShape.Texture1D, false, false) => Schema.TextureViewDimension.Texture1D,
            (SlangResourceShape.Texture1D, true, false) => Schema.TextureViewDimension.Texture1DArray,
            (SlangResourceShape.Texture2D, false, false) => Schema.TextureViewDimension.Texture2D,
            (SlangResourceShape.Texture2D, true, false) => Schema.TextureViewDimension.Texture2DArray,
            (SlangResourceShape.Texture2D, false, true) => Schema.TextureViewDimension.Texture2DMS,
            (SlangResourceShape.Texture2D, true, true) => Schema.TextureViewDimension.Texture2DMSArray,
            (SlangResourceShape.TextureCube, false, false) => Schema.TextureViewDimension.Cube,
            (SlangResourceShape.TextureCube, true, false) => Schema.TextureViewDimension.CubeArray,
            (SlangResourceShape.Texture3D, false, false) => Schema.TextureViewDimension.Texture3D,
            (SlangResourceShape.TextureSubpass, false, false) => Schema.TextureViewDimension.Texture2D,
            (SlangResourceShape.TextureSubpass, false, true) => Schema.TextureViewDimension.Texture2DMS,
            _ => null,
        };
    }

}

internal static partial class SlangBindingMap
{
    private static (Schema.AccessEffect Effect, Schema.ShaderQualifiers Qualifiers) ResolveEffect(
        VariableReflection variable,
        Schema.AccessEffect reflectedEffect,
        Schema.ShaderQualifiers reflectedQualifiers,
        Schema.DescriptorType kind)
    {
        if (reflectedEffect == Schema.AccessEffect.None)
            throw new InvalidDataException(
                $"Shader resource '{variable.Name}' has no reflected access effect.");

        AttributeReflection selected = FindResourceEffect(variable);
        if (selected == AttributeReflection.Null)
            return (reflectedEffect, reflectedQualifiers);
        if (kind == Schema.DescriptorType.Sampler)
            throw new InvalidDataException(
                $"Shader sampler '{variable.Name}' cannot declare AccessEffect.");
        if (selected.ArgumentCount != 2)
            throw new InvalidDataException(
                $"Shader resource '{variable.Name}' ResourceEffect must provide effects and qualifiers arguments.");

        Schema.AccessEffect declaredEffect = ParseEffect(variable.Name, selected);
        Schema.ShaderQualifiers declaredQualifiers = ParseQualifiers(variable.Name, selected);
        if (((int)declaredEffect & ~(int)reflectedEffect) != 0)
            throw new InvalidDataException(
                $"Shader resource '{variable.Name}' declares effect {declaredEffect} beyond its reflected effect {reflectedEffect}.");
        ValidateQualifiers(variable.Name, declaredEffect, declaredQualifiers, kind, reflectedEffect);
        return (declaredEffect, reflectedQualifiers | declaredQualifiers);
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

    private static Schema.AccessEffect ParseEffect(
        string resourceName,
        AttributeReflection attribute) => attribute.GetArgumentValueInt(0) switch
    {
        1 => Schema.AccessEffect.Read,
        2 => Schema.AccessEffect.Write,
        3 => Schema.AccessEffect.ReadWrite,
        int value => throw new InvalidDataException(
            $"Shader resource '{resourceName}' declares invalid ResourceEffects value {value}."),
    };

    private static Schema.ShaderQualifiers ParseQualifiers(
        string resourceName,
        AttributeReflection attribute)
    {
        int qualifierValue = attribute.GetArgumentValueInt(1);
        const int allQualifiers = (int)(
            Schema.ShaderQualifiers.Atomic |
            Schema.ShaderQualifiers.Append |
            Schema.ShaderQualifiers.Consume |
            Schema.ShaderQualifiers.RasterOrdered |
            Schema.ShaderQualifiers.Feedback);
        if (qualifierValue < 0 || (qualifierValue & ~allQualifiers) != 0)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' declares invalid ResourceQualifiers value {qualifierValue}.");
        return (Schema.ShaderQualifiers)qualifierValue;
    }

    private static void ValidateQualifiers(
        string resourceName,
        Schema.AccessEffect effect,
        Schema.ShaderQualifiers qualifiers,
        Schema.DescriptorType kind,
        Schema.AccessEffect reflectedEffect)
    {
        ValidateStorageQualifiers(resourceName, qualifiers, kind);
        ValidateAtomicQualifier(resourceName, effect, qualifiers, reflectedEffect);
        ValidateWriteQualifier(
            resourceName,
            effect,
            qualifiers,
            Schema.ShaderQualifiers.Append,
            "Append");
        ValidateReadQualifier(resourceName, effect, qualifiers);
        ValidateWriteQualifier(
            resourceName,
            effect,
            qualifiers,
            Schema.ShaderQualifiers.RasterOrdered,
            "RasterOrdered");
        ValidateFeedbackQualifier(resourceName, effect, qualifiers);
    }

    private static void ValidateStorageQualifiers(
        string resourceName,
        Schema.ShaderQualifiers qualifiers,
        Schema.DescriptorType kind)
    {
        bool storage = kind is Schema.DescriptorType.StorageBuffer or Schema.DescriptorType.StorageTexture;
        if (qualifiers != Schema.ShaderQualifiers.None && !storage)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' declares qualifiers on non-storage slot {kind}.");
    }

    private static void ValidateAtomicQualifier(
        string resourceName,
        Schema.AccessEffect effect,
        Schema.ShaderQualifiers qualifiers,
        Schema.AccessEffect reflectedEffect)
    {
        if ((qualifiers & Schema.ShaderQualifiers.Atomic) != 0 &&
            (effect != Schema.AccessEffect.ReadWrite ||
             reflectedEffect != Schema.AccessEffect.ReadWrite))
        {
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must declare ReadWrite and expose read-write access for Atomic qualifiers.");
        }
    }

    private static void ValidateWriteQualifier(
        string resourceName,
        Schema.AccessEffect effect,
        Schema.ShaderQualifiers qualifiers,
        Schema.ShaderQualifiers qualifier,
        string qualifierName)
    {
        if ((qualifiers & qualifier) != 0 &&
            effect is not (Schema.AccessEffect.Write or Schema.AccessEffect.ReadWrite))
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must include Write for {qualifierName} qualifiers.");
    }

    private static void ValidateReadQualifier(
        string resourceName,
        Schema.AccessEffect effect,
        Schema.ShaderQualifiers qualifiers)
    {
        if ((qualifiers & Schema.ShaderQualifiers.Consume) != 0 &&
            effect is not (Schema.AccessEffect.Read or Schema.AccessEffect.ReadWrite))
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must include Read for Consume qualifiers.");
    }

    private static void ValidateFeedbackQualifier(
        string resourceName,
        Schema.AccessEffect effect,
        Schema.ShaderQualifiers qualifiers)
    {
        if ((qualifiers & Schema.ShaderQualifiers.Feedback) != 0 &&
            effect != Schema.AccessEffect.ReadWrite)
            throw new InvalidDataException(
                $"Shader resource '{resourceName}' must declare ReadWrite for Feedback qualifiers.");
    }

    private static bool IsResourceEffectAttribute(string name) =>
        string.Equals(name, "ResourceEffect", StringComparison.Ordinal) ||
        string.Equals(name, "ResourceEffectAttribute", StringComparison.Ordinal) ||
        name.EndsWith("::ResourceEffect", StringComparison.Ordinal) ||
        name.EndsWith("::ResourceEffectAttribute", StringComparison.Ordinal);

    private static void ValidateAccessEffectTarget(VariableLayoutReflection layout)
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
                $"Shader variable '{variable.Name}' declares AccessEffect on a resource container instead of a resource-valued leaf.");
    }

    private static Schema.TextureSampleType? TextureSampleType(
        SlangResourceShape shape,
        SlangScalarType scalarType)
    {
        if ((shape & SlangResourceShape.TextureShadowFlag) != 0)
            return Schema.TextureSampleType.Depth;
        return scalarType switch
        {
            SlangScalarType.Float16 or SlangScalarType.Float32 or SlangScalarType.Float64 =>
                Schema.TextureSampleType.Float,
            SlangScalarType.UInt8 or SlangScalarType.UInt16 or SlangScalarType.UInt32 or
            SlangScalarType.UInt64 or SlangScalarType.UIntPtr => Schema.TextureSampleType.UInt,
            SlangScalarType.Int8 or SlangScalarType.Int16 or SlangScalarType.Int32 or
            SlangScalarType.Int64 or SlangScalarType.IntPtr => Schema.TextureSampleType.SInt,
            _ => null,
        };
    }

    private static Schema.StorageFormat? StorageFormat(SlangImageFormat format) => format switch
    {
        SlangImageFormat.RGBA32f => Schema.StorageFormat.R32G32B32A32Float,
        SlangImageFormat.RGBA16f => Schema.StorageFormat.R16G16B16A16Float,
        SlangImageFormat.RG32f => Schema.StorageFormat.R32G32Float,
        SlangImageFormat.RG16f => Schema.StorageFormat.R16G16Float,
        SlangImageFormat.R32f => Schema.StorageFormat.R32Float,
        SlangImageFormat.R16f => Schema.StorageFormat.R16Float,
        SlangImageFormat.RGBA8 => Schema.StorageFormat.R8G8B8A8UNorm,
        SlangImageFormat.RG8 => Schema.StorageFormat.R8G8UNorm,
        SlangImageFormat.R8 => Schema.StorageFormat.R8UNorm,
        SlangImageFormat.R32ui => Schema.StorageFormat.R32UInt,
        SlangImageFormat.R16ui => Schema.StorageFormat.R16UInt,
        _ => null,
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

    private static (byte ResourceType, Schema.DescriptorType Kind) ReadOnly(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferSrv, Schema.DescriptorType.ReadOnlyBuffer),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferSrv, Schema.DescriptorType.ReadOnlyBuffer),
            SlangResourceShape.AccelerationStructure =>
                (AccelStruct, Schema.DescriptorType.AccelerationStructure),
            SlangResourceShape.TextureSubpass =>
                (InputAttachment, Schema.DescriptorType.SampledTexture),
            _ => (TextureSrv, Schema.DescriptorType.SampledTexture),
        };

    private static (byte ResourceType, Schema.DescriptorType Kind) ReadWrite(SlangResourceShape shape)
        => shape switch
        {
            SlangResourceShape.ByteAddressBuffer =>
                (BufferUav, Schema.DescriptorType.StorageBuffer),
            SlangResourceShape.StructuredBuffer or SlangResourceShape.TextureBuffer =>
                (BufferUav, Schema.DescriptorType.StorageBuffer),
            _ => (TextureUav, Schema.DescriptorType.StorageTexture),
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
            Schema.ShaderStage.Node => 0x4000,
            _ => 0,
        };
}

