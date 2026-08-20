using System.Numerics;
using Silk.NET.Direct3D12;
using NativeBufferSrv = Silk.NET.Direct3D12.BufferSrv;
using NativeBufferUav = Silk.NET.Direct3D12.BufferUav;
using NativeDsvDesc = Silk.NET.Direct3D12.DepthStencilViewDesc;
using NativeRtvDesc = Silk.NET.Direct3D12.RenderTargetViewDesc;
using NativeSamplerDesc = Silk.NET.Direct3D12.SamplerDesc;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);
        RequireSameDevice(nativeDevice, buffer, nameof(desc));
        ValidateBufferView(buffer, desc.Range, BufferUsages.Constant, null, 0);
        BufferRange range = desc.Range.Resolve(buffer.Info.Size);
        if ((range.Offset & 255) != 0 || (range.Size & 255) != 0 || range.Size > 65_536)
            throw new ArgumentException("A CBV range must be 256-byte aligned and at most 64 KiB.", nameof(desc));
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(buffer.Info.CreationNodeMask))
            .Allocate();
        D3D12BufferCbv? result = null;
        try
        {
            WriteBufferCbv(nativeDevice, buffer, desc, descriptor.Cpu);
            result = new D3D12BufferCbv(nativeDevice, buffer, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);
        RequireSameDevice(nativeDevice, buffer, nameof(desc));
        ValidateBufferView(
            buffer,
            desc.Range,
            BufferUsages.ShaderRead,
            desc.Format,
            desc.StructureStride);
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(buffer.Info.CreationNodeMask))
            .Allocate();
        D3D12BufferSrv? result = null;
        try
        {
            WriteBufferSrv(nativeDevice, buffer, desc, descriptor.Cpu);
            result = new D3D12BufferSrv(nativeDevice, buffer, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);
        RequireSameDevice(nativeDevice, buffer, nameof(desc));
        ValidateBufferView(
            buffer,
            desc.Range,
            BufferUsages.ShaderWrite,
            desc.Format,
            desc.StructureStride);
        D3D12Buffer? counter = ResolveUavCounter(nativeDevice, desc);
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(buffer.Info.CreationNodeMask))
            .Allocate();
        D3D12BufferUav? result = null;
        try
        {
            WriteBufferUav(nativeDevice, buffer, counter, desc, descriptor.Cpu);
            result = new D3D12BufferUav(nativeDevice, buffer, counter, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);
        RequireSameDevice(nativeDevice, texture.Owner, nameof(desc));
        ValidateTextureView(
            texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            TextureViewKind.ShaderResource);
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(texture.Info.CreationNodeMask))
            .Allocate();
        D3D12TextureSrv? result = null;
        try
        {
            WriteTextureSrv(nativeDevice, texture, desc, descriptor.Cpu);
            result = new D3D12TextureSrv(nativeDevice, texture, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);
        RequireSameDevice(nativeDevice, texture.Owner, nameof(desc));
        ValidateTextureView(
            texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            TextureViewKind.UnorderedAccess);
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(texture.Info.CreationNodeMask))
            .Allocate();
        D3D12TextureUav? result = null;
        try
        {
            WriteTextureUav(nativeDevice, texture, desc, descriptor.Cpu);
            result = new D3D12TextureUav(nativeDevice, texture, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public ColorAttachmentView CreateColorAttachmentView(
        Device device,
        in ColorAttachmentViewDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);
        RequireSameDevice(nativeDevice, texture.Owner, nameof(desc));
        ValidateTextureView(
            texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            TextureViewKind.ColorAttachment);
        DescriptorLease descriptor = nativeDevice
            .GetRenderTargetDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(texture.Info.CreationNodeMask))
            .Allocate();
        D3D12ColorAttachmentView? result = null;
        try
        {
            WriteColorAttachmentView(nativeDevice, texture, desc, descriptor.Cpu);
            result = new D3D12ColorAttachmentView(nativeDevice, texture, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public DepthStencilView CreateDepthStencilView(
        Device device,
        in SomeEngine.Graphics.DepthStencilViewDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);
        RequireSameDevice(nativeDevice, texture.Owner, nameof(desc));
        ValidateTextureView(
            texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            TextureViewKind.DepthStencil);
        DescriptorLease descriptor = nativeDevice
            .GetDepthStencilDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(texture.Info.CreationNodeMask))
            .Allocate();
        D3D12DepthStencilView? result = null;
        try
        {
            WriteDepthStencilView(nativeDevice, texture, desc, descriptor.Cpu);
            result = new D3D12DepthStencilView(nativeDevice, texture, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public Sampler CreateSampler(Device device, in SomeEngine.Graphics.SamplerDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        ValidateSampler(desc);
        DescriptorLease descriptor = nativeDevice.SamplerDescriptors.Allocate();
        D3D12Sampler? result = null;
        try
        {
            WriteSampler(nativeDevice, desc, descriptor.Cpu);
            result = new D3D12Sampler(nativeDevice, desc, descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    private enum TextureViewKind : byte
    {
        ShaderResource,
        UnorderedAccess,
        ColorAttachment,
        DepthStencil,
    }

    private static void ReleaseFailedView(GraphicsObject? view, DescriptorLease descriptor)
    {
        if (view is null)
            descriptor.Release();
        else
            view.Dispose();
    }

    private static void ValidateBufferView(
        D3D12Buffer buffer,
        in BufferRange requestedRange,
        BufferUsages requiredUsage,
        Format? elementFormat,
        uint structureStride)
    {
        buffer.ThrowIfDisposed();
        if ((buffer.Info.Usages & requiredUsage) == 0)
            throw new ArgumentException($"The Buffer was not created for {requiredUsage} views.");
        if (elementFormat.HasValue && structureStride != 0)
            throw new ArgumentException("A Buffer view cannot be both typed and structured.");

        BufferRange range = requestedRange.Resolve(buffer.Info.Size);
        uint elementSize;
        if (elementFormat is Format typed)
        {
            if (FormatMappings.IsDepthStencil(typed) ||
                FormatMappings.IsBlockCompressed(typed) ||
                FormatMappings.IsSrgb(typed))
            {
                throw new ArgumentException("The format is not a D3D12 typed Buffer format.");
            }
            FormatSupport support = buffer.Device.Capabilities.GetFormatSupport(typed);
            FormatFeatures requiredFeatures = FormatFeatures.Buffer |
                (requiredUsage == BufferUsages.ShaderWrite
                    ? FormatFeatures.Storage | FormatFeatures.StorageStore
                    : FormatFeatures.ShaderLoad);
            RequireFormatFeatures(support, requiredFeatures, "typed Buffer view");
            elementSize = FormatMappings.BytesPerElement(typed);
        }
        else if (structureStride != 0)
        {
            if ((structureStride & 3) != 0 || structureStride > 2_048)
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
            throw new ArgumentException("The Buffer view range is not element-aligned.");
        if (range.Size / elementSize > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedRange), "The Buffer view has too many elements.");
    }

    private D3D12Buffer? ResolveUavCounter(
        D3D12Device device,
        in BufferUavDesc desc)
    {
        if (desc.CounterBuffer is null)
        {
            if (desc.CounterOffset != 0)
                throw new ArgumentException("A null UAV counter Buffer requires CounterOffset=0.", nameof(desc));
            return null;
        }
        if (desc.Format.HasValue || desc.StructureStride == 0)
            throw new ArgumentException("Only a structured UAV may name a counter Buffer.", nameof(desc));

        D3D12Buffer counter = RequireBuffer(desc.CounterBuffer);
        if (!ReferenceEquals(counter.Device, device))
            throw new ArgumentException("The UAV counter Buffer belongs to another Device.", nameof(desc));
        counter.ThrowIfDisposed();
        if ((counter.Info.Usages & BufferUsages.ShaderWrite) == 0)
            throw new ArgumentException("The UAV counter Buffer requires ShaderWrite usage.", nameof(desc));
        if ((desc.CounterOffset & 4_095) != 0 ||
            desc.CounterOffset > counter.Info.Size ||
            sizeof(uint) > counter.Info.Size - desc.CounterOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desc),
                "The UAV counter offset must be 4 KiB aligned and contain four bytes.");
        }
        return counter;
    }

    private static void ValidateTextureView(
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        TextureViewKind kind)
    {
        texture.Owner.ThrowIfDisposed();
        TextureInfo info = texture.Info;
        ValidateTextureViewUsageAndFormat(info, format, kind);
        ValidateTextureViewMipAndAspects(info, range, kind);
        ValidateTextureViewDimensionAndLayers(info, range, dimension, kind);
        ValidateTextureViewFormatSupport(texture, info, format, kind);
    }

    private static void ValidateTextureViewUsageAndFormat(
        in TextureInfo info,
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

        bool depthStencil = FormatMappings.IsDepthStencil(format);
        if (kind == TextureViewKind.DepthStencil)
        {
            if (!depthStencil)
                throw new ArgumentException("A DSV requires a depth/stencil format.");
        }
        else if (kind is TextureViewKind.UnorderedAccess or TextureViewKind.ColorAttachment)
        {
            if (depthStencil || FormatMappings.IsBlockCompressed(format))
                throw new ArgumentException("The format is incompatible with this writable view.");
            if (kind == TextureViewKind.UnorderedAccess && FormatMappings.IsSrgb(format))
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
            TextureAspects expected = FormatMappings.PlaneCount(info.Format) == 1
                ? TextureAspects.Depth
                : TextureAspects.Depth | TextureAspects.Stencil;
            TextureAspects planeExpected = FormatMappings.PlaneCount(info.Format) == 1
                ? TextureAspects.Plane0
                : TextureAspects.Plane0 | TextureAspects.Plane1;
            if (range.Aspects != expected && range.Aspects != planeExpected)
                throw new ArgumentException("A DSV selects the complete depth/stencil format.");
        }
        else
        {
            _ = FormatMappings.PlaneIndex(info.Format, range.Aspects);
        }
    }

    private static void ValidateTextureViewDimensionAndLayers(
        in TextureInfo info,
        in TextureSubresourceRange range,
        TextureViewDimension dimension,
        TextureViewKind kind)
    {
        if (!IsCompatibleTextureViewDimension(info, dimension))
            throw new ArgumentException("The view dimension is incompatible with the Texture.");
        if (kind == TextureViewKind.UnorderedAccess && info.SampleCount != 1)
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
        D3D12TextureResource texture,
        in TextureInfo info,
        Format format,
        TextureViewKind kind)
    {
        FormatSupport formatSupport = texture.Owner.Device.Capabilities.GetFormatSupport(format);
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

    private static void WriteBufferCbv(
        D3D12Device device,
        D3D12Buffer buffer,
        in BufferCbvDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Resolve(buffer.Info.Size);
        ConstantBufferViewDesc native = new()
        {
            BufferLocation = buffer.Native->GetGPUVirtualAddress() + range.Offset,
            SizeInBytes = checked((uint)range.Size),
        };
        device.Native->CreateConstantBufferView(&native, destination);
    }

    private static void WriteBufferSrv(
        D3D12Device device,
        D3D12Buffer buffer,
        in BufferSrvDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Resolve(buffer.Info.Size);
        GetBufferViewShape(
            range,
            desc.Format,
            desc.StructureStride,
            out DxgiFormat format,
            out ulong firstElement,
            out uint elementCount,
            out uint stride,
            out BufferSrvFlags flags);
        ShaderResourceViewDesc native = new()
        {
            Format = format,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 5768,
        };
        native.Buffer = new NativeBufferSrv
        {
            FirstElement = firstElement,
            NumElements = elementCount,
            StructureByteStride = stride,
            Flags = flags,
        };
        device.Native->CreateShaderResourceView(buffer.Native, &native, destination);
    }

    private static void WriteBufferUav(
        D3D12Device device,
        D3D12Buffer buffer,
        D3D12Buffer? counter,
        in BufferUavDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Resolve(buffer.Info.Size);
        GetBufferViewShape(
            range,
            desc.Format,
            desc.StructureStride,
            out DxgiFormat format,
            out ulong firstElement,
            out uint elementCount,
            out uint stride,
            out BufferSrvFlags srvFlags);
        UnorderedAccessViewDesc native = new()
        {
            Format = format,
            ViewDimension = UavDimension.Buffer,
        };
        native.Buffer = new NativeBufferUav
        {
            FirstElement = firstElement,
            NumElements = elementCount,
            StructureByteStride = stride,
            CounterOffsetInBytes = desc.CounterOffset,
            Flags = srvFlags == BufferSrvFlags.Raw ? BufferUavFlags.Raw : BufferUavFlags.None,
        };
        device.Native->CreateUnorderedAccessView(
            buffer.Native,
            counter is null ? null : counter.Native,
            &native,
            destination);
    }

    private static void GetBufferViewShape(
        in BufferRange range,
        Format? elementFormat,
        uint structureStride,
        out DxgiFormat format,
        out ulong firstElement,
        out uint elementCount,
        out uint stride,
        out BufferSrvFlags flags)
    {
        uint elementSize;
        if (elementFormat is Format typed)
        {
            format = FormatMappings.ToDxgi(typed);
            elementSize = FormatMappings.BytesPerElement(typed);
            stride = 0;
            flags = BufferSrvFlags.None;
        }
        else if (structureStride != 0)
        {
            format = DxgiFormat.FormatUnknown;
            elementSize = structureStride;
            stride = structureStride;
            flags = BufferSrvFlags.None;
        }
        else
        {
            format = DxgiFormat.FormatR32Typeless;
            elementSize = 4;
            stride = 0;
            flags = BufferSrvFlags.Raw;
        }

        firstElement = range.Offset / elementSize;
        elementCount = checked((uint)(range.Size / elementSize));
    }

    private static void WriteTextureSrv(
        D3D12Device device,
        D3D12TextureResource texture,
        in TextureSrvDesc desc,
        CpuDescriptorHandle destination)
    {
        TextureSubresourceRange range = desc.Range;
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, range.Aspects);
        ShaderResourceViewDesc native = new()
        {
            Format = FormatMappings.ToShaderViewFormat(desc.Format, range.Aspects),
            ViewDimension = ToSrvDimension(desc.Dimension),
            Shader4ComponentMapping = 5768,
        };

        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DSrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArraySrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DSrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DArray:
                native.Texture2DArray = new Tex2DArraySrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                native.Texture2DMSArray = new Tex2DmsArraySrv
                {
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Cube:
                native.TextureCube = new TexcubeSrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                };
                break;
            case TextureViewDimension.CubeArray:
                native.TextureCubeArray = new TexcubeArraySrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                    First2DArrayFace = range.FirstArrayLayer,
                    NumCubes = range.ArrayLayerCount / 6,
                };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DSrv
                {
                    MostDetailedMip = range.FirstMipLevel,
                    MipLevels = range.MipLevelCount,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(desc));
        }

        device.Native->CreateShaderResourceView(texture.Native, &native, destination);
    }

    private static void WriteTextureUav(
        D3D12Device device,
        D3D12TextureResource texture,
        in TextureUavDesc desc,
        CpuDescriptorHandle destination)
    {
        TextureSubresourceRange range = desc.Range;
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, range.Aspects);
        UnorderedAccessViewDesc native = new()
        {
            Format = FormatMappings.ToDxgi(desc.Format),
            ViewDimension = ToUavDimension(desc.Dimension),
        };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DUav { MipSlice = range.FirstMipLevel };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArrayUav
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DUav
                {
                    MipSlice = range.FirstMipLevel,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DArray:
                native.Texture2DArray = new Tex2DArrayUav
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DUav
                {
                    MipSlice = range.FirstMipLevel,
                    FirstWSlice = range.FirstArrayLayer,
                    WSize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Cube:
            case TextureViewDimension.CubeArray:
                native.ViewDimension = UavDimension.Texture2Darray;
                native.Texture2DArray = new Tex2DArrayUav
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
            case TextureViewDimension.Texture2DMultisampledArray:
                throw new NotSupportedException("Direct3D 12 does not support multisampled UAVs.");
            default:
                throw new ArgumentOutOfRangeException(nameof(desc));
        }
        device.Native->CreateUnorderedAccessView(texture.Native, null, &native, destination);
    }

    private static void WriteColorAttachmentView(
        D3D12Device device,
        D3D12TextureResource texture,
        in ColorAttachmentViewDesc desc,
        CpuDescriptorHandle destination)
    {
        TextureSubresourceRange range = desc.Range;
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, range.Aspects);
        NativeRtvDesc native = new()
        {
            Format = FormatMappings.ToDxgi(desc.Format),
            ViewDimension = ToRtvDimension(desc.Dimension),
        };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DRtv { MipSlice = range.FirstMipLevel };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArrayRtv
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DRtv
                {
                    MipSlice = range.FirstMipLevel,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DArray:
            case TextureViewDimension.Cube:
            case TextureViewDimension.CubeArray:
                native.ViewDimension = RtvDimension.Texture2Darray;
                native.Texture2DArray = new Tex2DArrayRtv
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                    PlaneSlice = plane,
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                native.Texture2DMSArray = new Tex2DmsArrayRtv
                {
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DRtv
                {
                    MipSlice = range.FirstMipLevel,
                    FirstWSlice = range.FirstArrayLayer,
                    WSize = range.ArrayLayerCount,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(desc));
        }
        device.Native->CreateRenderTargetView(texture.Native, &native, destination);
    }

    private static void WriteDepthStencilView(
        D3D12Device device,
        D3D12TextureResource texture,
        in SomeEngine.Graphics.DepthStencilViewDesc desc,
        CpuDescriptorHandle destination)
    {
        TextureSubresourceRange range = desc.Range;
        DsvFlags flags = DsvFlags.None;
        if (desc.ReadOnlyDepth)
            flags |= DsvFlags.ReadOnlyDepth;
        if (desc.ReadOnlyStencil)
            flags |= DsvFlags.ReadOnlyStencil;
        NativeDsvDesc native = new()
        {
            Format = FormatMappings.ToDxgi(desc.Format),
            ViewDimension = ToDsvDimension(desc.Dimension),
            Flags = flags,
        };
        switch (desc.Dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DDsv { MipSlice = range.FirstMipLevel };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArrayDsv
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DDsv { MipSlice = range.FirstMipLevel };
                break;
            case TextureViewDimension.Texture2DArray:
            case TextureViewDimension.Cube:
            case TextureViewDimension.CubeArray:
                native.ViewDimension = DsvDimension.Texture2Darray;
                native.Texture2DArray = new Tex2DArrayDsv
                {
                    MipSlice = range.FirstMipLevel,
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                native.Texture2DMSArray = new Tex2DmsArrayDsv
                {
                    FirstArraySlice = range.FirstArrayLayer,
                    ArraySize = range.ArrayLayerCount,
                };
                break;
            case TextureViewDimension.Texture3D:
                throw new NotSupportedException("Direct3D 12 does not support 3D depth/stencil views.");
            default:
                throw new ArgumentOutOfRangeException(nameof(desc));
        }
        device.Native->CreateDepthStencilView(texture.Native, &native, destination);
    }

    private static void WriteSampler(
        D3D12Device device,
        in SomeEngine.Graphics.SamplerDesc desc,
        CpuDescriptorHandle destination)
    {
        NativeSamplerDesc native = new()
        {
            Filter = ToFilter(desc),
            AddressU = ToAddressMode(desc.AddressU),
            AddressV = ToAddressMode(desc.AddressV),
            AddressW = ToAddressMode(desc.AddressW),
            MipLODBias = desc.MipLodBias,
            MaxAnisotropy = desc.MaximumAnisotropy,
            ComparisonFunc = desc.Comparison is CompareOperation comparison
                ? ToComparison(comparison)
                : ComparisonFunc.Always,
            MinLOD = desc.MinimumLod,
            MaxLOD = desc.MaximumLod,
        };
        native.BorderColor[0] = desc.BorderColor.X;
        native.BorderColor[1] = desc.BorderColor.Y;
        native.BorderColor[2] = desc.BorderColor.Z;
        native.BorderColor[3] = desc.BorderColor.W;
        device.Native->CreateSampler(&native, destination);
    }

    private static SrvDimension ToSrvDimension(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => SrvDimension.Texture1D,
        TextureViewDimension.Texture1DArray => SrvDimension.Texture1Darray,
        TextureViewDimension.Texture2D => SrvDimension.Texture2D,
        TextureViewDimension.Texture2DArray => SrvDimension.Texture2Darray,
        TextureViewDimension.Texture2DMultisampled => SrvDimension.Texture2Dms,
        TextureViewDimension.Texture2DMultisampledArray => SrvDimension.Texture2Dmsarray,
        TextureViewDimension.Cube => SrvDimension.Texturecube,
        TextureViewDimension.CubeArray => SrvDimension.Texturecubearray,
        TextureViewDimension.Texture3D => SrvDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static UavDimension ToUavDimension(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => UavDimension.Texture1D,
        TextureViewDimension.Texture1DArray => UavDimension.Texture1Darray,
        TextureViewDimension.Texture2D => UavDimension.Texture2D,
        TextureViewDimension.Texture2DArray or TextureViewDimension.Cube or
            TextureViewDimension.CubeArray => UavDimension.Texture2Darray,
        TextureViewDimension.Texture3D => UavDimension.Texture3D,
        TextureViewDimension.Texture2DMultisampled => UavDimension.Texture2Dms,
        TextureViewDimension.Texture2DMultisampledArray => UavDimension.Texture2Dmsarray,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static RtvDimension ToRtvDimension(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => RtvDimension.Texture1D,
        TextureViewDimension.Texture1DArray => RtvDimension.Texture1Darray,
        TextureViewDimension.Texture2D => RtvDimension.Texture2D,
        TextureViewDimension.Texture2DArray or TextureViewDimension.Cube or
            TextureViewDimension.CubeArray => RtvDimension.Texture2Darray,
        TextureViewDimension.Texture2DMultisampled => RtvDimension.Texture2Dms,
        TextureViewDimension.Texture2DMultisampledArray => RtvDimension.Texture2Dmsarray,
        TextureViewDimension.Texture3D => RtvDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static DsvDimension ToDsvDimension(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => DsvDimension.Texture1D,
        TextureViewDimension.Texture1DArray => DsvDimension.Texture1Darray,
        TextureViewDimension.Texture2D => DsvDimension.Texture2D,
        TextureViewDimension.Texture2DArray or TextureViewDimension.Cube or
            TextureViewDimension.CubeArray => DsvDimension.Texture2Darray,
        TextureViewDimension.Texture2DMultisampled => DsvDimension.Texture2Dms,
        TextureViewDimension.Texture2DMultisampledArray => DsvDimension.Texture2Dmsarray,
        TextureViewDimension.Texture3D =>
            throw new NotSupportedException("Direct3D 12 does not support 3D depth/stencil views."),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static Filter ToFilter(in SomeEngine.Graphics.SamplerDesc desc)
    {
        bool comparison = desc.Comparison.HasValue;
        if (desc.MaximumAnisotropy > 1)
            return comparison ? Filter.ComparisonAnisotropic : Filter.Anisotropic;

        return (desc.MinFilter, desc.MagFilter, desc.MipFilter, comparison) switch
        {
            (FilterType.Nearest, FilterType.Nearest, FilterType.Nearest, false) =>
                Filter.MinMagMipPoint,
            (FilterType.Nearest, FilterType.Nearest, FilterType.Linear, false) =>
                Filter.MinMagPointMipLinear,
            (FilterType.Nearest, FilterType.Linear, FilterType.Nearest, false) =>
                Filter.MinPointMagLinearMipPoint,
            (FilterType.Nearest, FilterType.Linear, FilterType.Linear, false) =>
                Filter.MinPointMagMipLinear,
            (FilterType.Linear, FilterType.Nearest, FilterType.Nearest, false) =>
                Filter.MinLinearMagMipPoint,
            (FilterType.Linear, FilterType.Nearest, FilterType.Linear, false) =>
                Filter.MinLinearMagPointMipLinear,
            (FilterType.Linear, FilterType.Linear, FilterType.Nearest, false) =>
                Filter.MinMagLinearMipPoint,
            (FilterType.Linear, FilterType.Linear, FilterType.Linear, false) =>
                Filter.MinMagMipLinear,
            (FilterType.Nearest, FilterType.Nearest, FilterType.Nearest, true) =>
                Filter.ComparisonMinMagMipPoint,
            (FilterType.Nearest, FilterType.Nearest, FilterType.Linear, true) =>
                Filter.ComparisonMinMagPointMipLinear,
            (FilterType.Nearest, FilterType.Linear, FilterType.Nearest, true) =>
                Filter.ComparisonMinPointMagLinearMipPoint,
            (FilterType.Nearest, FilterType.Linear, FilterType.Linear, true) =>
                Filter.ComparisonMinPointMagMipLinear,
            (FilterType.Linear, FilterType.Nearest, FilterType.Nearest, true) =>
                Filter.ComparisonMinLinearMagMipPoint,
            (FilterType.Linear, FilterType.Nearest, FilterType.Linear, true) =>
                Filter.ComparisonMinLinearMagPointMipLinear,
            (FilterType.Linear, FilterType.Linear, FilterType.Nearest, true) =>
                Filter.ComparisonMinMagLinearMipPoint,
            (FilterType.Linear, FilterType.Linear, FilterType.Linear, true) =>
                Filter.ComparisonMinMagMipLinear,
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
    }

    private static TextureAddressMode ToAddressMode(AddressType address) => address switch
    {
        AddressType.Repeat => TextureAddressMode.Wrap,
        AddressType.MirrorRepeat => TextureAddressMode.Mirror,
        AddressType.ClampToEdge => TextureAddressMode.Clamp,
        AddressType.ClampToBorder => TextureAddressMode.Border,
        AddressType.MirrorOnce => TextureAddressMode.MirrorOnce,
        _ => throw new ArgumentOutOfRangeException(nameof(address)),
    };

    private static ComparisonFunc ToComparison(CompareOperation comparison) => comparison switch
    {
        CompareOperation.Never => ComparisonFunc.Never,
        CompareOperation.Less => ComparisonFunc.Less,
        CompareOperation.Equal => ComparisonFunc.Equal,
        CompareOperation.LessOrEqual => ComparisonFunc.LessEqual,
        CompareOperation.Greater => ComparisonFunc.Greater,
        CompareOperation.NotEqual => ComparisonFunc.NotEqual,
        CompareOperation.GreaterOrEqual => ComparisonFunc.GreaterEqual,
        CompareOperation.Always => ComparisonFunc.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
    };
}
