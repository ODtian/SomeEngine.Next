using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDimension = Silk.NET.Direct3D12.ResourceDimension;
using NativeTextureLayout = Silk.NET.Direct3D12.TextureLayout;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        SamplerFeedback capability =
            nativeDevice.RequireCapability<SamplerFeedback>(nameof(CreateSamplerFeedbackTexture));
        D3D12TextureResource sampled = RequireTexture(desc.SampledTexture);
        RequireSameDevice(nativeDevice, sampled.Owner, nameof(desc));

        TextureInfo sampledInfo = sampled.Info;
        ValidateSamplerFeedbackDescription(capability, sampledInfo, desc);
        DxgiFormat opaqueFormat = desc.Type switch
        {
            SamplerFeedbackType.MinimumMip => DxgiFormat.FormatSamplerFeedbackMinMipOpaque,
            SamplerFeedbackType.MipRegionUsed => DxgiFormat.FormatSamplerFeedbackMipRegionUsedOpaque,
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
        ResourceDesc1 nativeDescription = new(
            NativeResourceDimension.Texture2D,
            0,
            sampledInfo.Width,
            sampledInfo.Height,
            checked((ushort)sampledInfo.ArrayLayerCount),
            checked((ushort)sampledInfo.MipLevelCount),
            opaqueFormat,
            new Silk.NET.DXGI.SampleDesc(1, 0),
            NativeTextureLayout.LayoutUnknown,
            ResourceFlags.AllowUnorderedAccess,
            new MipRegion(desc.MipRegionWidth, desc.MipRegionHeight, 1));

        HeapProperties properties = CreateHeapProperties(
            MemoryType.DeviceLocal,
            sampledInfo.CreationNodeMask,
            sampledInfo.VisibleNodeMask);
        NativeResource* native = CreateSamplerFeedbackResource(
            nativeDevice,
            properties,
            nativeDescription);

        D3D12SamplerFeedbackTexture? result = null;
        try
        {
            ResourceAllocationInfo allocation = nativeDevice.Native->GetResourceAllocationInfo2(
                sampledInfo.VisibleNodeMask,
                1,
                &nativeDescription,
                null);
            if (allocation.SizeInBytes == ulong.MaxValue ||
                allocation.Alignment < 64 * 1024)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12 returned invalid sampler-feedback allocation requirements.");
            }

            TextureInfo info = new(
                TextureDimension.Texture2D,
                sampledInfo.Width,
                sampledInfo.Height,
                1,
                sampledInfo.MipLevelCount,
                sampledInfo.ArrayLayerCount,
                1,
                Format.R8UInt,
                TextureUsages.Storage | TextureUsages.SamplerFeedback,
                MemoryType.DeviceLocal,
                ReadOnlySpan<Format>.Empty,
                0,
                allocation.SizeInBytes,
                sampledInfo.CreationNodeMask,
                sampledInfo.VisibleNodeMask);
            result = new D3D12SamplerFeedbackTexture(
                nativeDevice,
                native,
                info,
                desc);
            native = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
        finally
        {
            if (native is not null)
                _ = native->Release();
        }
    }

    private static void ValidateSamplerFeedbackDescription(
        SamplerFeedback capability,
        in TextureInfo sampled,
        in SamplerFeedbackTextureDesc description)
    {
        if (sampled.Dimension != TextureDimension.Texture2D ||
            sampled.SampleCount != 1 ||
            (sampled.Usages & TextureUsages.Sampled) == 0)
        {
            throw new ArgumentException(
                "Sampler feedback requires a single-sampled Texture2D with Sampled usage.",
                nameof(description));
        }
        if (!capability.SupportedFormats.Contains(sampled.Format))
        {
            throw new NotSupportedException(
                $"Format {sampled.Format} does not support sampler feedback.");
        }
        if (description.MipRegionWidth < capability.MinimumMipRegionWidth ||
            description.MipRegionHeight < capability.MinimumMipRegionHeight ||
            description.MipRegionWidth > sampled.Width / 2 ||
            description.MipRegionHeight > sampled.Height / 2 ||
            !BitOperations.IsPow2(description.MipRegionWidth) ||
            !BitOperations.IsPow2(description.MipRegionHeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                "Sampler-feedback mip-region dimensions must be supported powers of two.");
        }
    }

    private static NativeResource* CreateSamplerFeedbackResource(
        D3D12Device device,
        in HeapProperties properties,
        in ResourceDesc1 description)
    {
        NativeResource* result = null;
        Guid iid = NativeResource.Guid;
        fixed (HeapProperties* nativeProperties = &properties)
        fixed (ResourceDesc1* nativeDescription = &description)
        {
            int hr = device.EnhancedBarriers
                ? device.Native->CreateCommittedResource3(
                    nativeProperties,
                    Silk.NET.Direct3D12.HeapFlags.None,
                    nativeDescription,
                    BarrierLayout.Undefined,
                    null,
                    null,
                    0,
                    null,
                    &iid,
                    (void**)&result)
                : device.Native->CreateCommittedResource2(
                    nativeProperties,
                    Silk.NET.Direct3D12.HeapFlags.None,
                    nativeDescription,
                    ResourceStates.Common,
                    null,
                    null,
                    &iid,
                    (void**)&result);
            ThrowIfFailed(
                device,
                hr,
                NativeOperationType.Ordinary,
                device.EnhancedBarriers
                    ? "ID3D12Device10::CreateCommittedResource3(sampler feedback)"
                    : "ID3D12Device8::CreateCommittedResource2(sampler feedback)");
        }
        return result;
    }

    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<SamplerFeedback>(nameof(CreateSamplerFeedbackUav));
        D3D12SamplerFeedbackTexture feedback = RequireSamplerFeedbackTexture(texture);
        RequireSameDevice(nativeDevice, feedback, nameof(texture));
        TextureInfo info = feedback.Info;
        TextureSubresourceRange range = desc.Range;
        TextureViewDimension expectedDimension = info.ArrayLayerCount == 1
            ? TextureViewDimension.Texture2D
            : TextureViewDimension.Texture2DArray;
        if (!ReferenceEquals(texture, desc.Texture) ||
            desc.Format != Format.R8UInt ||
            desc.Dimension != expectedDimension ||
            range.FirstMipLevel != 0 ||
            range.MipLevelCount != info.MipLevelCount ||
            range.FirstArrayLayer != 0 ||
            range.ArrayLayerCount != info.ArrayLayerCount ||
            range.Aspects is not (TextureAspects.Color or TextureAspects.Plane0))
        {
            throw new ArgumentException(
                "A sampler-feedback UAV must describe the complete supplied feedback Texture.",
                nameof(desc));
        }
        D3D12TextureResource feedbackResource = feedback.NativeResource;
        D3D12TextureResource sampled = RequireTexture(feedback.SampledTexture);
        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(
                    feedbackResource.Info.CreationNodeMask))
            .Allocate();
        D3D12SamplerFeedbackUav? result = null;
        try
        {
            nativeDevice.Native->CreateSamplerFeedbackUnorderedAccessView(
                sampled.Native,
                feedbackResource.Native,
                descriptor.Cpu);
            result = new D3D12SamplerFeedbackUav(
                nativeDevice,
                feedbackResource,
                sampled,
                desc,
                descriptor);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
                descriptor.Release();
            else
                result.Dispose();
            throw;
        }
    }

    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ClearSamplerFeedback));
        D3D12SamplerFeedbackUav native = RequireSamplerFeedbackUav(feedback);

        command.PrepareCaptures(2, 1, 2);
        command.PrepareSwapchainUses(2);
        command.PrepareDescriptors(1, 0);
        command.PrepareTransientObjects((1 != 0 ? 1 : 0));
        (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) =
            command.StageSamplerFeedbackDescriptor(native.NativeDescriptor);
        command.Capture((TextureUav)native);
        command.Capture(native.SampledResource);
        uint* values = stackalloc uint[4] { 0, 0, 0, 0 };
        command.List->ClearUnorderedAccessViewUint(
            gpu,
            cpu,
            native.FeedbackResource.Native,
            values,
            0,
            null);
    }

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ResolveSamplerFeedback));
        D3D12SamplerFeedbackTexture nativeFeedback =
            RequireSamplerFeedbackTexture(feedback);
        D3D12Buffer nativeDestination = RequireBuffer(destination);

        if (nativeFeedback.Description.Type != SamplerFeedbackType.MinimumMip ||
            nativeFeedback.Info.ArrayLayerCount != 1)
        {
            throw new ArgumentException(
                "A Buffer decode is available only for a non-array MinimumMip feedback texture.",
                nameof(feedback));
        }
        if ((nativeDestination.Info.Usages & BufferUsages.CopyDestination) == 0)
            throw new ArgumentException("The destination Buffer requires CopyDestination usage.", nameof(destination));

        BufferRange range = destinationRange.Resolve(nativeDestination.Info.Size);
        ulong required = nativeFeedback.DecodedByteCount;
        if (range.Size < required || range.Offset > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationRange),
                "The destination range cannot contain the decoded feedback map.");
        }

        command.PrepareCaptures(3, 0, 3);
        command.PrepareSwapchainUses(2);
        command.Capture(nativeFeedback.NativeResource);
        command.Capture(nativeFeedback.SampledResource);
        command.Capture(nativeDestination);
        command.List->ResolveSubresourceRegion(
            nativeDestination.Native,
            0,
            checked((uint)range.Offset),
            0,
            nativeFeedback.NativeResource.Native,
            uint.MaxValue,
            null,
            DxgiFormat.FormatR8Uint,
            ResolveMode.DecodeSamplerFeedback);
    }

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ResolveSamplerFeedback));
        D3D12SamplerFeedbackTexture nativeFeedback =
            RequireSamplerFeedbackTexture(feedback);
        D3D12TextureResource nativeDestination = RequireTexture(destination);
        ValidateFeedbackTextureDestination(nativeFeedback, nativeDestination, destinationRange);

        command.PrepareCaptures(3, 0, 3);
        command.PrepareSwapchainUses(3);
        command.Capture(nativeFeedback.NativeResource);
        command.Capture(nativeFeedback.SampledResource);
        command.Capture(nativeDestination);
        for (uint layer = destinationRange.FirstArrayLayer;
             layer < destinationRange.FirstArrayLayer + destinationRange.ArrayLayerCount;
             layer++)
        for (uint mip = destinationRange.FirstMipLevel;
             mip < destinationRange.FirstMipLevel + destinationRange.MipLevelCount;
             mip++)
        {
            uint sourceSubresource = nativeFeedback.Description.Type ==
                SamplerFeedbackType.MinimumMip
                    ? uint.MaxValue
                    : checked(mip + layer * nativeFeedback.Info.MipLevelCount);
            uint destinationSubresource = NativeSubresource(
                nativeDestination.Info,
                mip,
                layer,
                TextureAspects.Color);
            command.List->ResolveSubresourceRegion(
                nativeDestination.Native,
                destinationSubresource,
                0,
                0,
                nativeFeedback.NativeResource.Native,
                sourceSubresource,
                null,
                DxgiFormat.FormatR8Uint,
                ResolveMode.DecodeSamplerFeedback);
        }
    }

    private static void ValidateFeedbackTextureDestination(
        D3D12SamplerFeedbackTexture feedback,
        D3D12TextureResource destination,
        in TextureSubresourceRange range)
    {
        TextureInfo info = destination.Info;
        uint mipEnd = checked(range.FirstMipLevel + range.MipLevelCount);
        uint layerEnd = checked(range.FirstArrayLayer + range.ArrayLayerCount);
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            mipEnd > info.MipLevelCount ||
            layerEnd > info.ArrayLayerCount ||
            layerEnd > feedback.Info.ArrayLayerCount ||
            range.Aspects is not (TextureAspects.Color or TextureAspects.Plane0) ||
            info.Dimension != TextureDimension.Texture2D ||
            info.Format != Format.R8UInt ||
            info.SampleCount != 1 ||
            (info.Usages & TextureUsages.CopyDestination) == 0 ||
            info.Width < feedback.DecodedWidth ||
            info.Height < feedback.DecodedHeight)
        {
            throw new ArgumentException(
                "The destination must be a sufficiently large R8UInt Texture2D decode target with matching subresources.",
                nameof(range));
        }

        if (feedback.Description.Type == SamplerFeedbackType.MinimumMip &&
            (range.FirstMipLevel != 0 || range.MipLevelCount != 1))
        {
            throw new ArgumentException(
                "MinimumMip feedback decodes to exactly one mip level.",
                nameof(range));
        }
        if (feedback.Description.Type == SamplerFeedbackType.MipRegionUsed &&
            mipEnd > feedback.Info.MipLevelCount)
        {
            throw new ArgumentException(
                "MipRegionUsed feedback has no matching source mip for the destination range.",
                nameof(range));
        }
    }

    private sealed class D3D12SamplerFeedbackTexture : SamplerFeedbackTexture
    {
        private readonly D3D12TextureResource _native;

        internal D3D12SamplerFeedbackTexture(
            D3D12Device device,
            NativeResource* native,
            TextureInfo info,
            in SamplerFeedbackTextureDesc description)
            : base(device, info, description)
        {
            SampledResource = RequireD3D12.Texture(description.SampledTexture);
            _native = new D3D12TextureResource(
                this,
                device,
                null,
                native,
                dependency: SampledResource.NativeLifetime);
            DecodedWidth = DivideRoundUp(info.Width, description.MipRegionWidth);
            DecodedHeight = DivideRoundUp(info.Height, description.MipRegionHeight);
            DecodedByteCount = checked((ulong)DecodedWidth * DecodedHeight);
        }

        internal D3D12TextureResource NativeResource => _native;
        internal D3D12TextureResource SampledResource { get; }
        internal uint DecodedWidth { get; }
        internal uint DecodedHeight { get; }
        internal ulong DecodedByteCount { get; }

        internal override void Release(bool fromParent) => _native.Release();

        private static uint DivideRoundUp(uint value, uint divisor) =>
            checked((value + divisor - 1) / divisor);
    }

    private sealed class D3D12SamplerFeedbackUav : SamplerFeedbackUav, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12SamplerFeedbackUav(
            D3D12Device device,
            D3D12TextureResource feedback,
            D3D12TextureResource sampled,
            in TextureUavDesc description,
            DescriptorLease descriptor)
            : base(device, description, sampled.Owner)
        {
            FeedbackResource = feedback;
            SampledResource = sampled;
            NativeDescriptor = descriptor;
            _references = new ViewReferences(
                device,
                descriptor,
                feedback.NativeLifetime,
                sampled.NativeLifetime);
        }

        internal D3D12TextureResource FeedbackResource { get; }
        internal D3D12TextureResource SampledResource { get; }
        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed partial class D3D12CommandContext
    {
        internal (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu)
            StageSamplerFeedbackDescriptor(DescriptorLease descriptor) =>
            Recording.StageSamplerFeedbackDescriptor(descriptor);
    }

    private sealed partial class D3D12CommandSlot
    {
        internal (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu)
            StageSamplerFeedbackDescriptor(DescriptorLease descriptor)
        {
            uint index = AllocateDescriptorPair(1, 0).ResourceBase;
            CpuDescriptorHandle cpu = GetCpuHandle(ParameterHeap.Resource, index);
            _context.NativeDevice.Native->CopyDescriptorsSimple(
                1,
                cpu,
                descriptor.Cpu,
                DescriptorHeapType.CbvSrvUav);
            return (cpu, GetGpuHandle(ParameterHeap.Resource, index));
        }

        private CpuDescriptorHandle GetCpuHandle(ParameterHeap heap, uint index)
        {
            DescriptorHeapType nativeType = heap == ParameterHeap.Resource
                ? DescriptorHeapType.CbvSrvUav
                : DescriptorHeapType.Sampler;
            ID3D12DescriptorHeap* descriptorHeap = heap == ParameterHeap.Resource
                ? _resourceArena
                : _samplerArena;
            CpuDescriptorHandle start = descriptorHeap->GetCPUDescriptorHandleForHeapStart();
            uint increment = _context.NativeDevice.Native
                ->GetDescriptorHandleIncrementSize(nativeType);
            return new CpuDescriptorHandle(
                start.Ptr + checked((nuint)(index * increment)));
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12SamplerFeedbackTexture SamplerFeedbackTexture(
            SamplerFeedbackTexture value) =>
            value as D3D12SamplerFeedbackTexture ??
            throw new ArgumentException(
                "The SamplerFeedbackTexture was not created by the Direct3D 12 backend.",
                nameof(value));

        internal static D3D12SamplerFeedbackUav SamplerFeedbackUav(
            SamplerFeedbackUav value) =>
            value as D3D12SamplerFeedbackUav ??
            throw new ArgumentException(
                "The SamplerFeedbackUav was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
