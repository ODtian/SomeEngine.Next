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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Buffer buffer = RequireBuffer(desc.Buffer);

        D3D12Buffer? counter = ResolveUavCounter(nativeDevice, desc);
        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12TextureResource texture = RequireTexture(desc.Texture);

        DescriptorAllocation descriptor = nativeDevice
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));

        DescriptorAllocation descriptor = nativeDevice.SamplerDescriptors.Allocate();
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

    private static void ReleaseFailedView(GraphicsObject? view, DescriptorAllocation descriptor)
    {
        if (view is null)
            descriptor.Release();
        else
            view.Dispose();
    }

    private D3D12Buffer? ResolveUavCounter(D3D12Device device, in BufferUavDesc desc) =>
        desc.CounterBuffer is null ? null : (D3D12Buffer)desc.CounterBuffer;

    private static void WriteBufferCbv(
        D3D12Device device,
        D3D12Buffer buffer,
        in BufferCbvDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Expand(buffer.Info.Size);
        ConstantBufferViewDesc native = new()
        {
            BufferLocation = ResourceTrace.GetGPUVirtualAddress(buffer.Native) + range.Offset,
            SizeInBytes = checked((uint)range.Size),
        };
        DeviceResourceTrace.CreateConstantBufferView(device.Native, &native, destination);
    }

    private static void WriteBufferSrv(
        D3D12Device device,
        D3D12Buffer buffer,
        in BufferSrvDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Expand(buffer.Info.Size);
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
        DeviceResourceTrace.CreateShaderResourceView(device.Native, buffer.Native, &native, destination);
    }

    private static void WriteBufferUav(
        D3D12Device device,
        D3D12Buffer buffer,
        D3D12Buffer? counter,
        in BufferUavDesc desc,
        CpuDescriptorHandle destination)
    {
        BufferRange range = desc.Range.Expand(buffer.Info.Size);
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
        DeviceResourceTrace.CreateUnorderedAccessView(
                device.Native, buffer.Native,
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

        TextureViewDimension dimension = desc.Dimension;
        if (dimension == TextureViewDimension.Cube && range.FirstArrayLayer != 0)
        {
            dimension = TextureViewDimension.CubeArray;
            native.ViewDimension = SrvDimension.Texturecubearray;
        }
        switch (dimension)
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
                break;
        }
        DeviceResourceTrace.CreateShaderResourceView(
                device.Native, texture.Native, &native, destination);
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
                break;
            default:
                break;
        }
        DeviceResourceTrace.CreateUnorderedAccessView(device.Native, texture.Native, null, &native, destination);
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
                break;
        }
        DeviceResourceTrace.CreateRenderTargetView(device.Native, texture.Native, &native, destination);
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
                break;
            default:
                break;
        }
        DeviceResourceTrace.CreateDepthStencilView(device.Native, texture.Native, &native, destination);
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
        DeviceResourceTrace.CreateSampler(device.Native, &native, destination);
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
        _ => default,
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
        _ => default,
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
        _ => default,
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
        _ => default,
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
            _ => default,
        };
    }

    private static TextureAddressMode ToAddressMode(AddressType address) => address switch
    {
        AddressType.Repeat => TextureAddressMode.Wrap,
        AddressType.MirrorRepeat => TextureAddressMode.Mirror,
        AddressType.ClampToEdge => TextureAddressMode.Clamp,
        AddressType.ClampToBorder => TextureAddressMode.Border,
        AddressType.MirrorOnce => TextureAddressMode.MirrorOnce,
        _ => default,
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
        _ => default,
    };
}
