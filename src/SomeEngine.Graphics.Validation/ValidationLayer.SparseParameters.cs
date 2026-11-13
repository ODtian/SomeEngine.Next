namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static void ValidateReservedPlacement(Device device, in ResourceNodePlacement placement)
    {
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil &&
            (placement.CreationNodeMask != 0 || placement.VisibleNodeMask != 0))
            throw new ArgumentException("Reserved resources receive node placement through their mapped tiles.", nameof(placement));
    }

    private static void ValidateReservedTexture(Device device, SparseResources capability, in TextureDesc desc)
    {
        ValidateReservedPlacement(device, desc.NodePlacement);
        ReadOnlySpan<Format> formats = desc.Dimension switch
        {
            TextureDimension.Texture2D => capability.SupportedTexture2DFormats,
            TextureDimension.Texture3D => capability.SupportedTexture3DFormats,
            _ => throw new NotSupportedException("The texture dimension does not support sparse resources."),
        };
        if (!formats.Contains(desc.Format) ||
            !device.Capabilities.GetFormatSupport(desc.Format).SupportsSparseSampleCount(desc.SampleCount))
            throw new NotSupportedException("The texture format and sample count do not support sparse resources.");
    }

    private SparseResourceInfo ReadSparseInfo(Resource resource)
    {
        SparseResourceInfo info = Backend.GetSparseResourceInfo(resource);
        if (info.TotalTileCount == 0)
            throw new ArgumentException("The resource was created without sparse storage.", nameof(resource));
        return info;
    }

    private static void ValidateSparseQueue(Queue queue)
    {
        if (queue.Device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Spirv &&
            (queue.Type != QueueType.Graphics || queue.Index != 0))
            throw new NotSupportedException("Sparse mappings use Graphics Queue 0 on this device.");
    }

    private static void ValidateSparseRegion(Device device, in SparseResourceInfo info, in SparseTileRegion region)
    {
        ReadOnlySpan<SparseSubresourceLayout> subresources = info.Subresources.Span;
        if (region.TileCount == 0 || region.Start.Subresource >= subresources.Length)
            throw new ArgumentOutOfRangeException(nameof(region));
        SparseSubresourceLayout layout = subresources[(int)region.Start.Subresource];
        if (region.Start.X >= layout.Width || region.Start.Y >= layout.Height || region.Start.Z >= layout.Depth)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (region.Boxed)
        {
            if (layout.Packed || region.Width == 0 || region.Height == 0 || region.Depth == 0 ||
                region.Width > layout.Width - region.Start.X || region.Height > layout.Height - region.Start.Y ||
                region.Depth > layout.Depth - region.Start.Z ||
                device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil &&
                (region.Height > ushort.MaxValue || region.Depth > ushort.MaxValue) ||
                (ulong)region.Width * region.Height * region.Depth != region.TileCount)
                throw new ArgumentOutOfRangeException(nameof(region));
            return;
        }
        ulong offset = ((ulong)region.Start.Z * layout.Height + region.Start.Y) * layout.Width + region.Start.X;
        ulong remaining = region.TileCount;
        while (remaining > layout.TileCount - offset)
        {
            remaining -= layout.TileCount - offset;
            if (layout.NextSubresource >= subresources.Length) throw new ArgumentOutOfRangeException(nameof(region));
            layout = subresources[(int)layout.NextSubresource];
            offset = 0;
        }
    }

    private void ValidateSparseMapping(Device device, in SparseMappingDesc mapping)
    {
        if (!Enum.IsDefined(mapping.Type)) throw new ArgumentOutOfRangeException(nameof(mapping));
        SparseResourceInfo info = ReadSparseInfo(mapping.Resource);
        ValidateSparseRegion(device, info, mapping.ResourceTiles);
        if (mapping.Type == SparseMappingType.Unmapped)
        {
            if (mapping.Heap is not null || mapping.HeapTileOffset != 0)
                throw new ArgumentException("Unmapped ranges name no heap.", nameof(mapping));
            return;
        }
        Heap heap = mapping.Heap ?? throw new ArgumentException("A mapped range requires a heap.", nameof(mapping));
        RequireOnDevice(device, heap, "Sparse heap");
        if (heap.Info.MemoryType != MemoryType.DeviceLocal)
            throw new ArgumentException("Sparse mappings require device-local heap memory.", nameof(mapping));
        if (device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil && mapping.HeapTileOffset > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mapping));
        ulong consumed = mapping.Type == SparseMappingType.Reused ? 1u : mapping.ResourceTiles.TileCount;
        ulong heapTiles = heap.Info.Size / info.Alignment;
        if (mapping.HeapTileOffset > heapTiles || consumed > heapTiles - mapping.HeapTileOffset)
            throw new ArgumentOutOfRangeException(nameof(mapping));
        HeapFlags requiredClass;
        bool shareable;
        if (mapping.Resource is Buffer buffer)
        {
            requiredClass = HeapFlags.Buffers;
            shareable = (buffer.Info.Usages & BufferUsages.Shareable) != 0;
        }
        else
        {
            Texture texture = (Texture)mapping.Resource;
            requiredClass = (texture.Info.Usages & (TextureUsages.ColorAttachment | TextureUsages.DepthStencilAttachment)) != 0
                ? HeapFlags.Attachments : HeapFlags.Textures;
            shareable = (texture.Info.Usages & TextureUsages.Shareable) != 0;
        }
        ValidatePlacement(heap, mapping.HeapTileOffset * info.Alignment,
            new MemoryRequirements(consumed * info.Alignment, info.Alignment, requiredClass), requiredClass, shareable);
    }

    private void ValidateResidencyInputs(Device device, ReadOnlySpan<ResidencyResource> resources)
    {
        if (resources.IsEmpty) throw new ArgumentException("At least one residency resource is required.", nameof(resources));
        foreach (ResidencyResource resource in resources)
        {
            if (resource.IsDefault || !ReferenceEquals(resource.Device, device))
                throw new ArgumentException("Residency resources belong to the supplied device.", nameof(resources));
            if (resource.Source is DeviceResource source) Require(source);
        }
    }
}
