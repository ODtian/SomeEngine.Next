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
        using var trace = RhiTrace.BeginResourceCreation();
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));

        D3D12TextureResource sampled = RequireTexture(desc.SampledTexture);

        TextureInfo sampledInfo = sampled.Info;

        DxgiFormat opaqueFormat = desc.Type switch
        {
            SamplerFeedbackType.MinimumMip => DxgiFormat.FormatSamplerFeedbackMinMipOpaque,
            SamplerFeedbackType.MipRegionUsed => DxgiFormat.FormatSamplerFeedbackMipRegionUsedOpaque,
            _ => default,
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
            ResourceAllocationInfo allocation = DeviceQueryTrace.GetResourceAllocationInfo2(
nativeDevice.Native, sampledInfo.VisibleNodeMask,
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
                _ = ResourceTrace.Release(native);
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
                ? DeviceResourceTrace.CreateCommittedResource3(
device.Native, nativeProperties,
                    Silk.NET.Direct3D12.HeapFlags.None,
                    nativeDescription,
                    BarrierLayout.Undefined,
                    null,
                    null,
                    0,
                    null,
                    &iid,
                    (void**)&result)
                : DeviceResourceTrace.CreateCommittedResource2(
device.Native, nativeProperties,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        
        D3D12SamplerFeedbackTexture feedback = RequireSamplerFeedbackTexture(texture);
        TextureInfo info = feedback.Info;
        TextureSubresourceRange range = desc.Range;
        TextureViewDimension expectedDimension = info.ArrayLayerCount == 1
            ? TextureViewDimension.Texture2D
            : TextureViewDimension.Texture2DArray;
        
        D3D12TextureResource feedbackResource = feedback.NativeResource;
        D3D12TextureResource sampled = RequireTexture(feedback.SampledTexture);
        DescriptorAllocation descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(
                    feedbackResource.Info.CreationNodeMask))
            .Allocate();
        D3D12SamplerFeedbackUav? result = null;
        try
        {
            DeviceResourceTrace.CreateSamplerFeedbackUnorderedAccessView(
                        nativeDevice.Native, sampled.Native,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        
        D3D12SamplerFeedbackUav native = RequireSamplerFeedbackUav(feedback);

        command.PrepareSwapchainUses(2);
        command.PrepareDescriptors(1, 0);
        command.PrepareTransientObjects((1 != 0 ? 1 : 0));
        (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) =
            command.StageSamplerFeedbackDescriptor(native.NativeDescriptor);
        uint* values = stackalloc uint[4] { 0, 0, 0, 0 };
        CommandExecutionTrace.ClearUnorderedAccessViewUint(
                command, command.List, gpu,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        
        D3D12SamplerFeedbackTexture nativeFeedback =
            RequireSamplerFeedbackTexture(feedback);
        D3D12Buffer nativeDestination = RequireBuffer(destination);

        BufferRange range = destinationRange.Expand(nativeDestination.Info.Size);
        ulong required = nativeFeedback.DecodedByteCount;

        command.PrepareSwapchainUses(2);
        CommandTransferTrace.ResolveSubresourceRegion(
                command, command.List, nativeDestination.Native,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        
        D3D12SamplerFeedbackTexture nativeFeedback =
            RequireSamplerFeedbackTexture(feedback);
        D3D12TextureResource nativeDestination = RequireTexture(destination);

        command.PrepareSwapchainUses(3);
        command.RecordSwapchainImage(nativeDestination);
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
                CommandTransferTrace.ResolveSubresourceRegion(
                            command, command.List, nativeDestination.Native,
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
                dependency: SampledResource.NativeData);
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
            DescriptorAllocation descriptor)
            : base(device, description, sampled.Texture)
        {
            FeedbackResource = feedback;
            SampledResource = sampled;
            NativeDescriptor = descriptor;
            _references = new ViewReferences(
                device,
                descriptor,
                feedback.NativeData,
                sampled.NativeData);
        }

        internal D3D12TextureResource FeedbackResource { get; }
        internal D3D12TextureResource SampledResource { get; }
        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed partial class D3D12CommandContext
    {
        internal (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu)
            StageSamplerFeedbackDescriptor(DescriptorAllocation descriptor) =>
            Recording.StageSamplerFeedbackDescriptor(descriptor);
    }

    private sealed partial class D3D12CommandBuffer
    {
        internal (CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu)
            StageSamplerFeedbackDescriptor(DescriptorAllocation descriptor)
        {
            uint index = AllocateDescriptorPair(1, 0).ResourceBase;
            CpuDescriptorHandle cpu = GetCpuHandle(ParameterHeap.Resource, index);

            DeviceQueryTrace.CopyDescriptorsSimple(
                        _context.NativeDevice.Native, 1,
                            cpu,
                            descriptor.Cpu,
                            DescriptorHeapType.CbvSrvUav);
            if (_resourceContents.Length < _resourceCapacity)
                Array.Resize(ref _resourceContents, checked((int)_resourceCapacity));
            _resourceContents[index] = new DescriptorContent(descriptor, new DescriptorEntryDesc(ResourceBindingType.TextureUav));
            return (cpu, GetGpuHandle(ParameterHeap.Resource, index));
        }

        private CpuDescriptorHandle GetCpuHandle(ParameterHeap heap, uint index)
        {
            DescriptorHeapType nativeType = heap == ParameterHeap.Resource
                ? DescriptorHeapType.CbvSrvUav
                : DescriptorHeapType.Sampler;
            CpuDescriptorHandle start = heap == ParameterHeap.Resource
                ? _resourceCpuStart
                : _samplerCpuStart;
            uint increment = _context.NativeDevice.DescriptorIncrement(nativeType);
            return new CpuDescriptorHandle(
                start.Ptr + checked((nuint)(index * increment)));
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12SamplerFeedbackTexture SamplerFeedbackTexture(
            SamplerFeedbackTexture value) =>(D3D12SamplerFeedbackTexture)value;

        internal static D3D12SamplerFeedbackUav SamplerFeedbackUav(
            SamplerFeedbackUav value) =>(D3D12SamplerFeedbackUav)value;
    }
}
