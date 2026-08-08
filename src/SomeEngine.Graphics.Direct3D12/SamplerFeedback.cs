using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using DxgiFormat = Silk.NET.DXGI.Format;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDimension = Silk.NET.Direct3D12.ResourceDimension;
using NativeTextureLayout = Silk.NET.Direct3D12.TextureLayout;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        SamplerFeedback capability =
            nativeDevice.RequireCapability<SamplerFeedback>(nameof(CreateSamplerFeedbackTexture));
        D3D12TextureResource sampled = NativeCast.Texture(desc.SampledTexture);

        TextureInfo sampledInfo = sampled.Info;
        if (sampledInfo.Dimension != TextureDimension.Texture2D ||
            sampledInfo.SampleCount != 1 ||
            (sampledInfo.Usages & TextureUsages.Sampled) == 0)
        {
            throw new ArgumentException(
                "Sampler feedback requires a single-sampled Texture2D with Sampled usage.",
                nameof(desc));
        }
        if (!capability.SupportedFormats.Contains(sampledInfo.Format))
        {
            throw new NotSupportedException(
                $"Format {sampledInfo.Format} does not support sampler feedback.");
        }
        if (desc.MipRegionWidth < capability.MinimumMipRegionWidth ||
            desc.MipRegionHeight < capability.MinimumMipRegionHeight ||
            desc.MipRegionWidth > sampledInfo.Width / 2 ||
            desc.MipRegionHeight > sampledInfo.Height / 2 ||
            !BitOperations.IsPow2(desc.MipRegionWidth) ||
            !BitOperations.IsPow2(desc.MipRegionHeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(desc),
                "Sampler-feedback mip-region dimensions must be supported powers of two.");
        }
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
            nativeDevice.EnabledNodeMask,
            nativeDevice.EnabledNodeMask);
        NativeResource* native = null;
        Guid iid = NativeResource.Guid;
        if (nativeDevice.EnhancedBarriers)
        {
            ThrowIfDeviceFailed(
                nativeDevice,
                nativeDevice.Native->CreateCommittedResource3(
                    &properties,
                    Silk.NET.Direct3D12.HeapFlags.None,
                    &nativeDescription,
                    BarrierLayout.Undefined,
                    null,
                    null,
                    0,
                    null,
                    &iid,
                    (void**)&native),
                "ID3D12Device10::CreateCommittedResource3(sampler feedback)");
        }
        else
        {
            ThrowIfDeviceFailed(
                nativeDevice,
                nativeDevice.Native->CreateCommittedResource2(
                    &properties,
                    Silk.NET.Direct3D12.HeapFlags.None,
                    &nativeDescription,
                    ResourceStates.Common,
                    null,
                    null,
                    &iid,
                    (void**)&native),
                "ID3D12Device8::CreateCommittedResource2(sampler feedback)");
        }

        D3D12SamplerFeedbackTexture? result = null;
        try
        {
            ResourceAllocationInfo allocation = nativeDevice.Native->GetResourceAllocationInfo2(
                nativeDevice.EnabledNodeMask,
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
                allocation.SizeInBytes);
            result = new D3D12SamplerFeedbackTexture(
                nativeDevice,
                native,
                info,
                desc);
            native = null;
            nativeDevice.RegisterChild(result);
            sampled.RegisterView(result);
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

    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        _ = nativeDevice.RequireCapability<SamplerFeedback>(nameof(CreateSamplerFeedbackUav));
        D3D12SamplerFeedbackTexture feedback = NativeCast.SamplerFeedbackTexture(texture);
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
        D3D12TextureResource sampled = NativeCast.Texture(feedback.SampledTexture);
        DescriptorLease descriptor = nativeDevice.ResourceDescriptors.Allocate();
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
            feedbackResource.RegisterView(result);
            sampled.RegisterView(result);
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
        D3D12CommandContext command = NativeCast.CommandContext(context);
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ClearSamplerFeedback));
        D3D12SamplerFeedbackUav native = NativeCast.SamplerFeedbackUav(feedback);

        (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) =
            command.StageSamplerFeedbackDescriptor(native.NativeDescriptor);
        command.Capture((TextureUav)native);
        command.Capture(native.SampledResource);
        uint* values = stackalloc uint[4];
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
        D3D12CommandContext command = NativeCast.CommandContext(context);
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ResolveSamplerFeedback));
        D3D12SamplerFeedbackTexture nativeFeedback =
            NativeCast.SamplerFeedbackTexture(feedback);
        D3D12Buffer nativeDestination = NativeCast.Buffer(destination);

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
        D3D12CommandContext command = NativeCast.CommandContext(context);
        _ = command.NativeDevice.RequireCapability<SamplerFeedback>(nameof(ResolveSamplerFeedback));
        D3D12SamplerFeedbackTexture nativeFeedback =
            NativeCast.SamplerFeedbackTexture(feedback);
        D3D12TextureResource nativeDestination = NativeCast.Texture(destination);
        ValidateFeedbackTextureDestination(nativeFeedback, nativeDestination, destinationRange);

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
        private int _released;

        internal D3D12SamplerFeedbackTexture(
            D3D12Device device,
            NativeResource* native,
            TextureInfo info,
            in SamplerFeedbackTextureDesc description)
            : base(device, info, description)
        {
            _native = new D3D12TextureResource(this, device, null, native);
            SampledResource = NativeCast.Texture(description.SampledTexture);
            DecodedWidth = DivideRoundUp(info.Width, description.MipRegionWidth);
            DecodedHeight = DivideRoundUp(info.Height, description.MipRegionHeight);
            DecodedByteCount = checked((ulong)DecodedWidth * DecodedHeight);
        }

        internal D3D12TextureResource NativeResource => _native;
        internal D3D12TextureResource SampledResource { get; }
        internal uint DecodedWidth { get; }
        internal uint DecodedHeight { get; }
        internal ulong DecodedByteCount { get; }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            SampledResource.UnregisterView(this);
            _native.Release();
        }

        private static uint DivideRoundUp(uint value, uint divisor) =>
            checked((value + divisor - 1) / divisor);
    }

    private sealed class D3D12SamplerFeedbackUav : SamplerFeedbackUav, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

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
            _lifetime = new ViewLifetime(
                device,
                descriptor,
                texture: feedback,
                pairedTexture: sampled);
        }

        internal D3D12TextureResource FeedbackResource { get; }
        internal D3D12TextureResource SampledResource { get; }
        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
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
            uint index = AllocateDescriptors(ParameterHeap.Resource, 1);
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

    private static partial class NativeCast
    {
        internal static D3D12SamplerFeedbackTexture SamplerFeedbackTexture(
            SamplerFeedbackTexture value)
        {
#if DEBUG
            return (D3D12SamplerFeedbackTexture)value;
#else
            return System.Runtime.CompilerServices.Unsafe
                .As<SamplerFeedbackTexture, D3D12SamplerFeedbackTexture>(ref value);
#endif
        }

        internal static D3D12SamplerFeedbackUav SamplerFeedbackUav(
            SamplerFeedbackUav value)
        {
#if DEBUG
            return (D3D12SamplerFeedbackUav)value;
#else
            return System.Runtime.CompilerServices.Unsafe
                .As<SamplerFeedbackUav, D3D12SamplerFeedbackUav>(ref value);
#endif
        }
    }
}
