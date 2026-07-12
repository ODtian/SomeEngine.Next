using Vortice.Direct3D12;
using Vortice.DXGI;
using NativeResidencyPriority = Vortice.Direct3D12.ResidencyPriority;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    public DeviceCapabilities Capabilities => new(
        SupportsTraditionalBinding: true,
        SupportsIndirectDraw: true,
        SupportsIndirectDrawIndexed: true,
        SupportsIndirectDispatch: true,
        SupportsTimestampQueries: true,
        SupportsOcclusionQueries: true,
        SupportsPipelineStatisticsQueries: true,
        SupportsSwapchain: true,
        SupportsPipelineCache: true,
        SupportsMemoryBudget: true,
        SupportsBindless: false,
        SupportsMeshShaders: false,
        SupportsVariableRateShading: false,
        SupportsRayTracing: false,
        SupportsSparseResources: false,
        SupportsSamplerFeedback: false,
        SupportsWorkGraphs: false,
        HighestShaderModel: PortableShaderModel(_native.HighestShaderModel),
        Limits: new DeviceLimits(
            MaxBufferSize: uint.MaxValue,
            MaxTextureDimension1D: 16_384,
            MaxTextureDimension2D: 16_384,
            MaxTextureDimension3D: 2_048,
            MaxTextureArrayLayers: 2_048,
            MaxBindGroups: 64,
            MaxBindingsPerGroup: 64,
            MaxDescriptorArrayLength: 1_000_000,
            MaxPushConstantBytes: 256,
            MinConstantBufferOffsetAlignment: 256,
            MinStorageBufferOffsetAlignment: 16,
            TextureDataPitchAlignment: 256,
            TextureDataPlacementAlignment: 512));

    private static Version PortableShaderModel(ShaderModel shaderModel) => shaderModel switch
    {
        ShaderModel.Model5_1 => new Version(5, 1),
        ShaderModel.Model6_0 => new Version(6, 0),
        ShaderModel.Model6_1 => new Version(6, 1),
        ShaderModel.Model6_2 => new Version(6, 2),
        ShaderModel.Model6_3 => new Version(6, 3),
        ShaderModel.Model6_4 => new Version(6, 4),
        ShaderModel.Model6_5 => new Version(6, 5),
        ShaderModel.Model6_6 => new Version(6, 6),
        ShaderModel.Model6_7 => new Version(6, 7),
        ShaderModel.Model6_8 => new Version(6, 8),
        ShaderModel.Model6_9 => new Version(6, 9),
        _ => throw new ArgumentOutOfRangeException(nameof(shaderModel)),
    };

    public FormatSupport GetFormatSupport(Format format)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!Enum.IsDefined(format) || format == Format.Unknown) return FormatSupport.None;

        FeatureDataFormatSupport query = new() { Format = Mappings.Format(format) };
        if (!_native.Device.CheckFeatureSupport(Vortice.Direct3D12.Feature.FormatSupport, ref query)) return FormatSupport.None;

        return MapFormatSupport(query.Support1);
    }

    private static FormatSupport MapFormatSupport(FormatSupport1 nativeSupport)
    {
        FormatSupport support = FormatSupport.None;
        ReadOnlySpan<(FormatSupport1 Native, FormatSupport Portable)> mappings =
        [
            (FormatSupport1.ShaderLoad | FormatSupport1.ShaderSample, FormatSupport.Sampled),
            (FormatSupport1.TypedUnorderedAccessView, FormatSupport.Storage),
            (FormatSupport1.RenderTarget, FormatSupport.RenderTarget),
            (FormatSupport1.DepthStencil, FormatSupport.DepthStencil),
            (FormatSupport1.InputAssemblerVertexBuffer, FormatSupport.VertexBuffer),
            (FormatSupport1.InputAssemblerIndexBuffer, FormatSupport.IndexBuffer),
            (FormatSupport1.Display, FormatSupport.Present),
            (FormatSupport1.MultisampleResolve, FormatSupport.Resolve),
            (FormatSupport1.Buffer | FormatSupport1.Texture1D | FormatSupport1.Texture2D | FormatSupport1.Texture3D, FormatSupport.Copy),
        ];
        foreach ((FormatSupport1 native, FormatSupport portable) in mappings)
            if ((nativeSupport & native) != 0) support |= portable;
        return support;
    }

    public MemoryBudget GetMemoryBudget(MemoryType memoryType)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));

        using IDXGIAdapter3 adapter = _native.Adapter.QueryInterface<IDXGIAdapter3>();
        QueryVideoMemoryInfo info = adapter.QueryVideoMemoryInfo(
            0,
            memoryType == MemoryType.DeviceLocal ? MemorySegmentGroup.Local : MemorySegmentGroup.NonLocal);
        return MemoryBudget.FromUsage(info.Budget, info.CurrentUsage);
    }

    public ResourceMemoryInfo GetResourceMemoryInfo(ResourceHandle resource)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        return resource.Kind switch
        {
            ResourceKind.Buffer => BufferMemoryInfo(resource),
            ResourceKind.Texture => TextureMemoryInfo(resource),
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };
    }

    public void SetResidencyPriority(ResourceHandle resource, SomeEngine.Graphics.ResidencyPriority priority)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));

        ID3D12Pageable pageable;
        switch (resource.Kind)
        {
            case ResourceKind.Buffer:
            {
                NativeBuffer buffer = GetBuffer(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
                buffer.Priority = priority;
                pageable = buffer.Resource;
                break;
            }
            case ResourceKind.Texture:
            {
                NativeTexture texture = GetTexture(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
                texture.Priority = priority;
                pageable = texture.Resource;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(resource));
        }

        using ID3D12Device1 device = _native.Device.QueryInterface<ID3D12Device1>();
        device.SetResidencyPriority(1, [pageable], [MapResidencyPriority(priority)]);
    }

    public BindlessTableHandle CreateBindlessTable(in BindlessTableDesc desc)
    {
        desc.Validate();
        throw new NotSupportedException(
            "This D3D12 backend exposes the mandatory traditional binding path only; bindless requires an explicitly advertised optional profile.");
    }

    public void DestroyBindlessTable(BindlessTableHandle table) => ThrowBindlessUnsupported();
    public BindlessSlot AllocateBindlessSlot(BindlessTableHandle table) => throw BindlessUnsupported();
    public void FreeBindlessSlot(in BindlessSlot slot) => ThrowBindlessUnsupported();
    public void WriteBindlessTexture(in BindlessSlot slot, TextureViewHandle view) => ThrowBindlessUnsupported();
    public void WriteBindlessBuffer(in BindlessSlot slot, BufferViewHandle view) => ThrowBindlessUnsupported();
    public void WriteBindlessSampler(in BindlessSlot slot, SamplerHandle sampler) => ThrowBindlessUnsupported();

    private ResourceMemoryInfo BufferMemoryInfo(ResourceHandle resource)
    {
        NativeBuffer buffer = GetBuffer(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
        return new ResourceMemoryInfo(
            resource,
            buffer.MemoryType,
            buffer.Allocation.Size,
            buffer.Allocation.Offset,
            buffer.Priority,
            Resident: true);
    }

    private ResourceMemoryInfo TextureMemoryInfo(ResourceHandle resource)
    {
        NativeTexture texture = GetTexture(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
        return new ResourceMemoryInfo(
            resource,
            texture.MemoryType,
            texture.Allocation.Size,
            texture.Allocation.Offset,
            texture.Priority,
            Resident: true);
    }

    private static NativeResidencyPriority MapResidencyPriority(SomeEngine.Graphics.ResidencyPriority priority) => priority switch
    {
        SomeEngine.Graphics.ResidencyPriority.Minimum => NativeResidencyPriority.Minimum,
        SomeEngine.Graphics.ResidencyPriority.Low => NativeResidencyPriority.Low,
        SomeEngine.Graphics.ResidencyPriority.Normal => NativeResidencyPriority.Normal,
        SomeEngine.Graphics.ResidencyPriority.High => NativeResidencyPriority.High,
        SomeEngine.Graphics.ResidencyPriority.Critical => NativeResidencyPriority.Maximum,
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    private static NotSupportedException BindlessUnsupported() => new(
        "Bindless is not advertised by this device. Use bind groups through the traditional binding path.");

    private static void ThrowBindlessUnsupported() => throw BindlessUnsupported();
}
