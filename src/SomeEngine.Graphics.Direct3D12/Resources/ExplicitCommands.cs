using Vortice.Direct3D12;
using Vortice.Mathematics;
using D3D12Range = Vortice.Direct3D12.Range;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    internal ID3D12Resource CreatePatternUpload(ulong size, uint pattern)
    {
        ResourceDescription description = ResourceDescription.Buffer(size);
        ID3D12Resource resource = _native.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapType.Upload,
            description,
            ResourceStates.GenericRead);
        try
        {
            int length = checked((int)size);
            Span<byte> mapped = resource.Map<byte>(0, length);
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, pattern);
            for (int index = 0; index < mapped.Length; index++) mapped[index] = bytes[index & 3];
            resource.Unmap(0, new D3D12Range(UIntPtr.Zero, new UIntPtr(size)));
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    internal NativeCpuDescriptor[] CreateColorClearDescriptors(
        NativeTexture texture,
        in TextureSubresourceRange requested)
    {
        TextureSubresourceRangeValidation.Normalize(texture.Desc, requested,
            out int firstMip, out int mipCount, out int firstLayer, out int layerCount, out TextureAspect aspects);
        if (aspects != TextureAspect.Color)
            throw new ArgumentException("A color clear must select only the color aspect.", nameof(requested));
        List<NativeCpuDescriptor> result = [];
        try
        {
            for (int mip = firstMip; mip < firstMip + mipCount; mip++)
            {
                int views = texture.Desc.Dimension == TextureDimension.Texture3D ? 1 : layerCount;
                for (int index = 0; index < views; index++)
                {
                    int layer = texture.Desc.Dimension == TextureDimension.Texture3D ? 0 : firstLayer + index;
                    result.Add(CreateColorClearDescriptor(texture, mip, layer));
                }
            }
            return result.ToArray();
        }
        catch
        {
            foreach (NativeCpuDescriptor descriptor in result) descriptor.Dispose();
            throw;
        }
    }

    private NativeCpuDescriptor CreateColorClearDescriptor(NativeTexture texture, int mip, int layer)
    {
        ValidatedTextureViewRange range = new(mip, 1, layer, 1, TextureAspect.Color);
        RenderTargetViewDescription description = CreateRenderTargetViewDescription(
            texture.Desc,
            texture.Desc.Format,
            range,
            ClearViewDimension(texture.Desc));
        return CreateCpuDescriptor(
            DescriptorHeapType.RenderTargetView,
            destination => _native.Device.CreateRenderTargetView(texture.Resource, description, destination));
    }

    internal (NativeCpuDescriptor[] Descriptors, ClearFlags Flags) CreateDepthStencilClearDescriptors(
        NativeTexture texture,
        in TextureSubresourceRange requested)
    {
        TextureSubresourceRangeValidation.Normalize(texture.Desc, requested,
            out int firstMip, out int mipCount, out int firstLayer, out int layerCount, out TextureAspect aspects);
        if ((aspects & TextureAspect.Color) != 0)
            throw new ArgumentException("A depth-stencil clear cannot select the color aspect.", nameof(requested));
        ClearFlags flags = GetClearFlags(aspects, requested);
        List<NativeCpuDescriptor> result = [];
        try
        {
            for (int mip = firstMip; mip < firstMip + mipCount; mip++)
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            {
                result.Add(CreateDepthStencilClearDescriptor(texture, mip, layer, aspects));
            }
            return (result.ToArray(), flags);
        }
        catch
        {
            foreach (NativeCpuDescriptor descriptor in result) descriptor.Dispose();
            throw;
        }
    }

    private NativeCpuDescriptor CreateDepthStencilClearDescriptor(
        NativeTexture texture,
        int mip,
        int layer,
        TextureAspect aspects)
    {
        ValidatedTextureViewRange range = new(mip, 1, layer, 1, aspects);
        DepthStencilViewDescription description = CreateDepthStencilViewDescription(
            texture.Desc,
            texture.Desc.Format,
            range,
            ClearViewDimension(texture.Desc),
            DepthStencilViewFlags.None);
        return CreateCpuDescriptor(
            DescriptorHeapType.DepthStencilView,
            destination => _native.Device.CreateDepthStencilView(texture.Resource, description, destination));
    }

    private static ClearFlags GetClearFlags(TextureAspect aspects, in TextureSubresourceRange requested)
    {
        ClearFlags flags = 0;
        if ((aspects & TextureAspect.Depth) != 0) flags |= ClearFlags.Depth;
        if ((aspects & TextureAspect.Stencil) != 0) flags |= ClearFlags.Stencil;
        if (flags == 0) throw new ArgumentException("A depth-stencil clear selects at least one aspect.", nameof(requested));
        return flags;
    }

    private static TextureViewDimension ClearViewDimension(in TextureDesc desc) => desc.Dimension switch
    {
        TextureDimension.Texture1D => desc.ArrayLayers == 1
            ? TextureViewDimension.Texture1D
            : TextureViewDimension.Texture1DArray,
        TextureDimension.Texture2D when desc.SampleCount > 1 => desc.ArrayLayers == 1
            ? TextureViewDimension.Texture2DMS
            : TextureViewDimension.Texture2DMSArray,
        TextureDimension.Texture2D => desc.ArrayLayers == 1
            ? TextureViewDimension.Texture2D
            : TextureViewDimension.Texture2DArray,
        TextureDimension.Texture3D => TextureViewDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(desc)),
    };
}
