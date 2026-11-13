using System.Numerics;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private enum TextureViewKind : byte { ShaderResource, UnorderedAccess, ColorAttachment, DepthStencil }

    private static bool IsDepthFormat(Format format) => (FormatLayouts.Get(format).Aspects & TextureAspects.Depth) != 0;
    private static bool IsCompressedFormat(Format format) => FormatLayouts.Get(format).IsCompressed;
    private static bool IsSrgbFormat(Format format) => FormatLayouts.Get(format).IsSrgb;
    private static uint FormatPlaneCount(Format format) => FormatLayouts.Get(format).PlaneCount;
    private static uint FormatPlaneIndex(Format format, TextureAspects aspect) => (IsDepthFormat(format), format, aspect) switch
    {
        (false, _, TextureAspects.Color or TextureAspects.Plane0) => 0,
        (true, _, TextureAspects.Depth or TextureAspects.Plane0) => 0,
        (true, Format.D24UNormS8UInt or Format.D32FloatS8UInt, TextureAspects.Stencil or TextureAspects.Plane1) => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(aspect)),
    };

    private static uint FormatElementSize(Format format) => FormatLayouts.Get(format).BlockBytes;

    private static Format ViewFormatFamily(Format format) => FormatLayouts.Get(format).Family;
    private static void ValidateHeapDescription(Device device, in HeapDesc desc)
    {
        if (desc.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "A Heap size must be nonzero.");
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil
            ? desc.Alignment is not (0 or 65_536 or 4_194_304)
            : desc.Alignment == 0 || !BitOperations.IsPow2(desc.Alignment))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap alignment is not supported by D3D12.");
        if (!Enum.IsDefined(desc.MemoryType))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap memory type is unknown.");
        const SomeEngine.Graphics.HeapFlags knownFlags =
            SomeEngine.Graphics.HeapFlags.Buffers |
            SomeEngine.Graphics.HeapFlags.Textures |
            SomeEngine.Graphics.HeapFlags.Attachments |
            SomeEngine.Graphics.HeapFlags.Shareable;
        if ((desc.Flags & ~knownFlags) != 0 ||
            device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Spirv && desc.Flags == HeapFlags.None)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap flags contain unknown bits.");
        uint enabled = device.EnabledNodeMask;
        if (desc.CreationNodeMask == 0 ||
            !BitOperations.IsPow2(desc.CreationNodeMask) ||
            desc.VisibleNodeMask == 0 ||
            (desc.CreationNodeMask & ~enabled) != 0 ||
            (desc.VisibleNodeMask & ~enabled) != 0 ||
            (desc.CreationNodeMask & desc.VisibleNodeMask) != desc.CreationNodeMask)
        {
            throw new ArgumentException(
                "A Heap requires exactly one enabled creation node, a nonempty enabled visibility set, " +
                "and visibility of its creation node.",
                nameof(desc));
        }
    }

    private static void ValidateBufferMemoryType(in BufferDesc desc, MemoryType memoryType)
    {
        const BufferUsages uploadUsages =
            BufferUsages.CopySource |
            BufferUsages.Constant |
            BufferUsages.ShaderRead |
            BufferUsages.Vertex |
            BufferUsages.Index |
            BufferUsages.Indirect |
            BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication |
            BufferUsages.Shareable;
        const BufferUsages readbackUsages =
            BufferUsages.CopyDestination |
            BufferUsages.QueryResolve |
            BufferUsages.Shareable;
        bool valid = memoryType switch
        {
            MemoryType.DeviceLocal => true,
            MemoryType.Upload => (desc.Usages & ~uploadUsages) == 0,
            MemoryType.Readback => (desc.Usages & ~readbackUsages) == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"The Buffer usage is incompatible with {memoryType} memory.",
                nameof(desc));
        }
    }

    private static void ValidateTextureDescription(Device device, in TextureDesc desc)
    {
        if (!Enum.IsDefined(desc.Dimension) || !Enum.IsDefined(desc.Format))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture dimension is unknown.");
        const TextureUsages knownUsages =
            TextureUsages.CopySource |
            TextureUsages.CopyDestination |
            TextureUsages.Sampled |
            TextureUsages.Storage |
            TextureUsages.ColorAttachment |
            TextureUsages.DepthStencilAttachment |
            TextureUsages.ShadingRate |
            TextureUsages.SamplerFeedback |
            TextureUsages.Shareable;
        if ((desc.Usages & ~knownUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture usage contains unknown bits.");
        if ((desc.Usages & TextureUsages.SamplerFeedback) != 0)
        {
            throw new ArgumentException(
                "TextureUsages.SamplerFeedback is reserved for CreateSamplerFeedbackTexture.",
                nameof(desc));
        }

        ValidateTextureShape(device, desc);
        ValidateResourceNodePlacement(device, desc.NodePlacement);
        ValidateTextureMipCount(desc);
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Spirv &&
            (desc.Usages == TextureUsages.None || desc.SampleCount > 64))
            throw new ArgumentOutOfRangeException(nameof(desc));
        ValidateMultisampling(device, desc);
        ValidateTextureFormatUsage(desc);
        ValidatePermittedViewFormats(desc);
    }

    private static void ValidateTextureShape(Device device, in TextureDesc desc)
    {
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0 ||
            desc.MipLevelCount == 0 || desc.ArrayLayerCount == 0 || desc.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "Texture dimensions, counts and sample count must be nonzero.");
        }
        if (desc.Dimension == TextureDimension.Texture1D &&
            (desc.Height != 1 || desc.Depth != 1))
            throw new ArgumentException("A 1D Texture has Height=1 and Depth=1.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture2D && desc.Depth != 1)
            throw new ArgumentException("A 2D Texture has Depth=1.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture3D && desc.ArrayLayerCount != 1)
            throw new ArgumentException("A 3D Texture has exactly one array layer.", nameof(desc));
        uint depthOrArray = desc.Dimension == TextureDimension.Texture3D
            ? desc.Depth
            : desc.ArrayLayerCount;
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil &&
            (depthOrArray > ushort.MaxValue || desc.MipLevelCount > ushort.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(desc), "Texture depth/array and mip counts must fit D3D12 fields.");
    }

    private static void ValidateTextureMipCount(in TextureDesc desc)
    {
        uint largestDimension = desc.Dimension switch
        {
            TextureDimension.Texture1D => desc.Width,
            TextureDimension.Texture2D => Math.Max(desc.Width, desc.Height),
            TextureDimension.Texture3D => Math.Max(desc.Width, Math.Max(desc.Height, desc.Depth)),
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
        uint maximumMipCount = checked((uint)BitOperations.Log2(largestDimension) + 1);
        if (desc.MipLevelCount > maximumMipCount)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture has more mip levels than its dimensions permit.");
    }

    private static void ValidateMultisampling(Device device, in TextureDesc desc)
    {
        bool multisampled = desc.SampleCount != 1;
        if (multisampled &&
            (!BitOperations.IsPow2(desc.SampleCount) ||
             desc.Dimension != TextureDimension.Texture2D ||
             desc.MipLevelCount != 1 ||
             device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil &&
             ((desc.Usages & TextureUsages.Storage) != 0 || IsCompressedFormat(desc.Format))))
        {
            throw new ArgumentException("The multisampled Texture description is not representable by D3D12.", nameof(desc));
        }
    }

    private static void ValidateTextureFormatUsage(in TextureDesc desc)
    {
        bool depthStencil = IsDepthFormat(desc.Format);
        if (depthStencil && (desc.Usages & TextureUsages.ColorAttachment) != 0)
            throw new ArgumentException("A depth/stencil format cannot be a color attachment.", nameof(desc));
        if (!depthStencil && (desc.Usages & TextureUsages.DepthStencilAttachment) != 0)
            throw new ArgumentException("A color format cannot be a depth/stencil attachment.", nameof(desc));
        if (depthStencil && (desc.Usages & TextureUsages.Storage) != 0)
            throw new ArgumentException("D3D12 depth/stencil formats cannot be storage textures.", nameof(desc));
        if (depthStencil &&
            desc.Dimension == TextureDimension.Texture3D &&
            (desc.Usages & TextureUsages.DepthStencilAttachment) != 0)
            throw new ArgumentException("D3D12 does not support 3D depth/stencil attachments.", nameof(desc));
        if (IsCompressedFormat(desc.Format) &&
            (desc.Usages & (TextureUsages.Storage |
                            TextureUsages.ColorAttachment |
                            TextureUsages.DepthStencilAttachment |
                            TextureUsages.ShadingRate |
                            TextureUsages.SamplerFeedback)) != 0)
        {
            throw new ArgumentException("A block-compressed format has an incompatible Texture usage.", nameof(desc));
        }
    }

    private static void ValidatePermittedViewFormats(in TextureDesc desc)
    {
        Format family = ViewFormatFamily(desc.Format);
        for (int index = 0; index < desc.PermittedViewFormats.Length; index++)
        {
            Format permitted = desc.PermittedViewFormats[index];
            if (ViewFormatFamily(permitted) != family)
            {
                throw new ArgumentException(
                    "Every permitted view format must belong to the Texture format family.",
                    nameof(desc));
            }
            for (int previous = 0; previous < index; previous++)
            {
                if (desc.PermittedViewFormats[previous] == permitted)
                    throw new ArgumentException("Permitted view formats must be unique.", nameof(desc));
            }
        }
    }

    private static void ValidatePlacement(
        Heap heap,
        ulong offset,
        in MemoryRequirements requirements,
        SomeEngine.Graphics.HeapFlags requiredClass,
        bool shareable)
    {
        if (offset % requirements.Alignment != 0 ||
            offset > heap.Info.Size ||
            requirements.Size > heap.Info.Size - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The placed resource range is misaligned or escapes the Heap.");
        }
        SomeEngine.Graphics.HeapFlags classes = heap.Info.Flags &
            (SomeEngine.Graphics.HeapFlags.Buffers |
             SomeEngine.Graphics.HeapFlags.Textures |
             SomeEngine.Graphics.HeapFlags.Attachments);
        bool unrestricted = classes == SomeEngine.Graphics.HeapFlags.None ||
            BitOperations.PopCount((uint)classes) > 1;
        if (!unrestricted && (classes & requiredClass) == 0)
            throw new ArgumentException("The Heap class is incompatible with the placed resource.", nameof(heap));
        if (shareable && (heap.Info.Flags & SomeEngine.Graphics.HeapFlags.Shareable) == 0)
            throw new ArgumentException("A shareable resource requires a shareable Heap.", nameof(heap));
    }

    private static void ValidatePlacedResourceNodePlacement(
        in ResourceNodePlacement placement,
        in HeapInfo heap,
        string parameterName)
    {
        if (placement.CreationNodeMask == 0 && placement.VisibleNodeMask == 0)
            return;
        if (placement.CreationNodeMask != heap.CreationNodeMask ||
            placement.VisibleNodeMask != heap.VisibleNodeMask)
        {
            throw new ArgumentException(
                "A placed resource inherits the creation and visibility masks of its Heap.",
                parameterName);
        }
    }

    private static void ValidateBufferView(
        Buffer buffer,
        in BufferRange requestedRange,
        BufferUsages requiredUsage,
        Format? elementFormat,
        uint structureStride)
    {
        if ((buffer.Info.Usages & requiredUsage) == 0)
            throw new ArgumentException($"The Buffer was not created for {requiredUsage} views.");
        if (elementFormat.HasValue && structureStride != 0)
            throw new ArgumentException("A Buffer view cannot be both typed and structured.");

        BufferRange range = requestedRange.Resolve(buffer.Info.Size);
        uint elementSize;
        if (elementFormat is Format typed)
        {
            if (!Enum.IsDefined(typed)) throw new ArgumentOutOfRangeException(nameof(elementFormat));
            if (IsDepthFormat(typed) ||
                IsCompressedFormat(typed) ||
                IsSrgbFormat(typed))
            {
                throw new ArgumentException("The format is not a D3D12 typed Buffer format.");
            }
            FormatSupport support = buffer.Device.Capabilities.GetFormatSupport(typed);
            FormatFeatures requiredFeatures = FormatFeatures.Buffer |
                (requiredUsage == BufferUsages.ShaderWrite
                    ? FormatFeatures.Storage | FormatFeatures.StorageStore
                    : FormatFeatures.ShaderLoad);
            RequireFormatFeatures(support, requiredFeatures, "typed Buffer view");
            elementSize = FormatElementSize(typed);
        }
        else if (structureStride != 0)
        {
            if ((structureStride & 3) != 0 || buffer.Device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil && structureStride > 2_048)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(structureStride),
                    "A structured Buffer stride must be a multiple of four and at most 2048 bytes.");
            }
            elementSize = structureStride;
        }
        else
        {
            elementSize = 4;
        }

        if (range.Offset % elementSize != 0 || range.Size % elementSize != 0)
        {
            throw new ArgumentException(
                $"The Buffer view range is not element-aligned: " +
                $"buffer='{buffer.Label}', offset={range.Offset}, size={range.Size}, " +
                $"elementSize={elementSize}.");
        }
        if (buffer.Device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil && requiredUsage != BufferUsages.Constant && elementFormat is null && structureStride == 0 && (range.Offset & 15) != 0)
            throw new ArgumentException("A raw Buffer view offset must be 16-byte aligned.", nameof(requestedRange));
        if (range.Size / elementSize > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedRange), "The Buffer view has too many elements.");
    }

    private static void ValidateTextureView(
        Texture texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        TextureViewKind kind)
    {
        TextureInfo info = texture.Info;
        ValidateTextureViewUsageAndFormat(texture.Device, info, format, kind);
        ValidateTextureViewMipAndAspects(info, range, kind);
        ValidateTextureViewDimensionAndLayers(texture.Device, info, range, dimension, kind);
        ValidateTextureViewFormatSupport(texture, info, format, kind);
    }

    private static void ValidateTextureViewUsageAndFormat(
        Device device, in TextureInfo info,
        Format format,
        TextureViewKind kind)
    {
        if ((info.Usages & TextureUsages.SamplerFeedback) != 0)
        {
            throw new ArgumentException(
                "Sampler-feedback Textures require CreateSamplerFeedbackUav and cannot be used with ordinary Texture views.");
        }
        TextureUsages requiredUsage = kind switch
        {
            TextureViewKind.ShaderResource => TextureUsages.Sampled,
            TextureViewKind.UnorderedAccess => TextureUsages.Storage,
            TextureViewKind.ColorAttachment => TextureUsages.ColorAttachment,
            TextureViewKind.DepthStencil => TextureUsages.DepthStencilAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if ((info.Usages & requiredUsage) == 0)
            throw new ArgumentException($"The Texture was not created for {requiredUsage} views.");

        bool declared = format == info.Format;
        foreach (Format permitted in info.PermittedViewFormats)
            declared |= permitted == format;
        if (!declared)
            throw new ArgumentException("The view format was not declared when the Texture was created.");

        bool depthStencil = IsDepthFormat(format);
        if (kind == TextureViewKind.DepthStencil)
        {
            if (!depthStencil)
                throw new ArgumentException("A DSV requires a depth/stencil format.");
        }
        else if (kind is TextureViewKind.UnorderedAccess or TextureViewKind.ColorAttachment)
        {
            if (depthStencil || IsCompressedFormat(format))
                throw new ArgumentException("The format is incompatible with this writable view.");
            if (kind == TextureViewKind.UnorderedAccess && IsSrgbFormat(format) &&
                device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil)
                throw new ArgumentException("D3D12 does not support sRGB UAV formats.");
        }
    }

    private static void ValidateTextureViewMipAndAspects(
        in TextureInfo info,
        in TextureSubresourceRange range,
        TextureViewKind kind)
    {
        if (range.MipLevelCount == 0 ||
            range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel)
            throw new ArgumentOutOfRangeException(nameof(range), "The Texture view mip range is invalid.");
        if (kind != TextureViewKind.ShaderResource && range.MipLevelCount != 1)
            throw new ArgumentException("A writable or attachment view selects exactly one mip level.");

        if (kind == TextureViewKind.DepthStencil)
        {
            TextureAspects expected = FormatPlaneCount(info.Format) == 1
                ? TextureAspects.Depth
                : TextureAspects.Depth | TextureAspects.Stencil;
            TextureAspects planeExpected = FormatPlaneCount(info.Format) == 1
                ? TextureAspects.Plane0
                : TextureAspects.Plane0 | TextureAspects.Plane1;
            if (range.Aspects != expected && range.Aspects != planeExpected)
                throw new ArgumentException("A DSV selects the complete depth/stencil format.");
        }
        else
        {
            _ = FormatPlaneIndex(info.Format, range.Aspects);
        }
    }

    private static void ValidateTextureViewDimensionAndLayers(
        Device device, in TextureInfo info,
        in TextureSubresourceRange range,
        TextureViewDimension dimension,
        TextureViewKind kind)
    {
        if (!IsCompatibleTextureViewDimension(info, dimension))
            throw new ArgumentException("The view dimension is incompatible with the Texture.");
        if (kind == TextureViewKind.UnorderedAccess && info.SampleCount != 1 &&
            device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil)
            throw new NotSupportedException("Direct3D 12 does not support multisampled UAVs.");
        if (kind == TextureViewKind.DepthStencil && info.Dimension == TextureDimension.Texture3D)
            throw new NotSupportedException("Direct3D 12 does not support 3D depth/stencil views.");

        ValidateTextureViewLayerRange(info, range, kind);
        ValidateTextureViewLayerShape(info, range, dimension, kind);
    }

    private static bool IsCompatibleTextureViewDimension(
        in TextureInfo info,
        TextureViewDimension dimension) => info.Dimension switch
        {
            TextureDimension.Texture1D => dimension is
                TextureViewDimension.Texture1D or TextureViewDimension.Texture1DArray,
            TextureDimension.Texture2D when info.SampleCount == 1 => dimension is
                TextureViewDimension.Texture2D or
                TextureViewDimension.Texture2DArray or
                TextureViewDimension.Cube or
                TextureViewDimension.CubeArray,
            TextureDimension.Texture2D => dimension is
                TextureViewDimension.Texture2DMultisampled or
                TextureViewDimension.Texture2DMultisampledArray,
            TextureDimension.Texture3D => dimension == TextureViewDimension.Texture3D,
            _ => false,
        };

    private static void ValidateTextureViewLayerRange(
        in TextureInfo info,
        in TextureSubresourceRange range,
        TextureViewKind kind)
    {
        uint layerLimit = info.Dimension == TextureDimension.Texture3D &&
            kind is TextureViewKind.UnorderedAccess or TextureViewKind.ColorAttachment
                ? Math.Max(1u, info.Depth >> checked((int)range.FirstMipLevel))
                : info.ArrayLayerCount;
        if (range.ArrayLayerCount == 0 ||
            range.FirstArrayLayer >= layerLimit ||
            range.ArrayLayerCount > layerLimit - range.FirstArrayLayer)
            throw new ArgumentOutOfRangeException(nameof(range), "The Texture view layer range is invalid.");
    }

    private static void ValidateTextureViewLayerShape(
        in TextureInfo info,
        in TextureSubresourceRange range,
        TextureViewDimension dimension,
        TextureViewKind kind)
    {
        bool arrayView = dimension is
            TextureViewDimension.Texture1DArray or
            TextureViewDimension.Texture2DArray or
            TextureViewDimension.Texture2DMultisampledArray or
            TextureViewDimension.Cube or
            TextureViewDimension.CubeArray;
        if (!arrayView && dimension != TextureViewDimension.Texture3D &&
            (info.ArrayLayerCount != 1 || range.FirstArrayLayer != 0 || range.ArrayLayerCount != 1))
            throw new ArgumentException("A non-array view requires a single-layer Texture.");
        if (dimension == TextureViewDimension.Texture3D && kind == TextureViewKind.ShaderResource &&
            (range.FirstArrayLayer != 0 || range.ArrayLayerCount != 1))
            throw new ArgumentException("A 3D SRV covers the mip's complete depth.");
        if (dimension == TextureViewDimension.Cube &&
            (range.FirstArrayLayer % 6 != 0 || range.ArrayLayerCount != 6))
            throw new ArgumentException("A Cube view selects exactly six aligned faces.");
        if (dimension == TextureViewDimension.CubeArray &&
            (range.FirstArrayLayer % 6 != 0 || range.ArrayLayerCount % 6 != 0))
            throw new ArgumentException("A CubeArray view selects complete aligned cubes.");
        if (info.SampleCount != 1 &&
            (range.FirstMipLevel != 0 || range.MipLevelCount != 1))
            throw new ArgumentException("A multisampled Texture view selects its only mip level.");
    }

    private static void ValidateTextureViewFormatSupport(
        Texture texture,
        in TextureInfo info,
        Format format,
        TextureViewKind kind)
    {
        FormatSupport formatSupport = texture.Device.Capabilities.GetFormatSupport(format);
        FormatFeatures requiredFormatFeatures = kind switch
        {
            TextureViewKind.ShaderResource => FormatFeatures.ShaderLoad |
                (info.SampleCount > 1 ? FormatFeatures.MultisampleLoad : FormatFeatures.None),
            TextureViewKind.UnorderedAccess =>
                FormatFeatures.Storage | FormatFeatures.StorageStore,
            TextureViewKind.ColorAttachment => FormatFeatures.ColorAttachment |
                (info.SampleCount > 1
                    ? FormatFeatures.MultisampleColorAttachment
                    : FormatFeatures.None),
            TextureViewKind.DepthStencil => FormatFeatures.DepthStencilAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        RequireFormatFeatures(formatSupport, requiredFormatFeatures, "Texture view");
        if (!formatSupport.SupportsSampleCount(info.SampleCount))
        {
            throw new NotSupportedException(
                $"Format {format} does not support {info.SampleCount}x samples for this view.");
        }
    }

    private static void RequireFormatFeatures(
        in FormatSupport support,
        FormatFeatures required,
        string operation)
    {
        if ((support.Features & required) != required)
        {
            throw new NotSupportedException(
                $"Format {support.Format} does not provide the features required by {operation}: " +
                $"{required}.");
        }
    }

    private static void ValidateSampler(in SomeEngine.Graphics.SamplerDesc desc)
    {
        if (!Enum.IsDefined(desc.MinFilter) ||
            !Enum.IsDefined(desc.MagFilter) ||
            !Enum.IsDefined(desc.MipFilter) ||
            !Enum.IsDefined(desc.AddressU) ||
            !Enum.IsDefined(desc.AddressV) ||
            !Enum.IsDefined(desc.AddressW) ||
            desc.Comparison is CompareOperation comparison && !Enum.IsDefined(comparison))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Sampler contains an unknown enum value.");
        if (desc.MaximumAnisotropy is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(desc), "Sampler anisotropy must be in [1, 16].");
        if (!float.IsFinite(desc.MipLodBias) ||
            !float.IsFinite(desc.MinimumLod) ||
            !float.IsFinite(desc.MaximumLod) ||
            desc.MinimumLod > desc.MaximumLod)
            throw new ArgumentException("The Sampler LOD range and bias must be finite and ordered.", nameof(desc));
    }


    private static void ValidateBufferDescription(Device device, in BufferDesc desc, MemoryType memoryType)
    {
        if (desc.Size == 0) throw new ArgumentOutOfRangeException(nameof(desc));
        const BufferUsages known = BufferUsages.CopySource | BufferUsages.CopyDestination | BufferUsages.Constant |
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Vertex | BufferUsages.Index |
            BufferUsages.Indirect | BufferUsages.AccelerationStructure | BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication | BufferUsages.StreamOutput | BufferUsages.QueryResolve | BufferUsages.Shareable;
        if ((desc.Usages & ~known) != 0 ||
            device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Spirv && desc.Usages == BufferUsages.None)
            throw new ArgumentOutOfRangeException(nameof(desc));
        ValidateBufferMemoryType(desc, memoryType);
        ValidateResourceNodePlacement(device, desc.NodePlacement);
    }

    private static void ValidateResourceNodePlacement(Device device, in ResourceNodePlacement placement)
    {
        uint enabled = device.EnabledNodeMask;
        uint creation = placement.CreationNodeMask == 0 ? 1u << BitOperations.TrailingZeroCount(enabled) : placement.CreationNodeMask;
        uint visible = placement.VisibleNodeMask == 0 ? creation : placement.VisibleNodeMask;
        if (!BitOperations.IsPow2(creation) || visible == 0 || (creation & ~enabled) != 0 ||
            (visible & ~enabled) != 0 || (visible & creation) != creation)
            throw new ArgumentException("Resource node masks must select enabled device nodes.", nameof(placement));
    }

    private static void ValidateConstantBufferRange(Device device, Buffer buffer, in BufferRange requested)
    {
        ValidateBufferView(buffer, requested, BufferUsages.Constant, null, 0);
        BufferRange range = requested.Resolve(buffer.Info.Size);
        uint alignment = device.Capabilities.Limits.ConstantBufferAlignment;
        if (range.Offset % alignment != 0 ||
            device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil && range.Size % alignment != 0 ||
            range.Size > device.Capabilities.Limits.MaximumConstantBufferRange)
            throw new ArgumentException("The constant buffer view range is misaligned or too large.", nameof(requested));
    }

    private static void ValidateCounter(Device device, in BufferUavDesc desc)
    {
        if (desc.CounterBuffer is null)
        {
            if (desc.CounterOffset != 0) throw new ArgumentException("A counter offset requires a counter buffer.", nameof(desc));
            return;
        }
        if (desc.Format.HasValue || desc.StructureStride == 0) throw new ArgumentException("A counter belongs to a structured UAV.", nameof(desc));
        if ((desc.CounterBuffer.Info.Usages & BufferUsages.ShaderWrite) == 0) throw new ArgumentException("A counter buffer requires ShaderWrite.", nameof(desc));
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil && (desc.CounterOffset & 4095u) != 0) throw new ArgumentOutOfRangeException(nameof(desc.CounterOffset));
        ValidateBufferRange(desc.CounterBuffer, desc.CounterOffset, sizeof(uint));
    }
}
