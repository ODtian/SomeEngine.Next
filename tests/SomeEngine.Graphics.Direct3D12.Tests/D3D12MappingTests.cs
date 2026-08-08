using SomeEngine.Graphics.Direct3D12;
using Xunit;
using NativeBarrierAccess = Silk.NET.Direct3D12.BarrierAccess;
using NativeBarrierLayout = Silk.NET.Direct3D12.BarrierLayout;
using NativeBarrierSync = Silk.NET.Direct3D12.BarrierSync;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeResourceStates = Silk.NET.Direct3D12.ResourceStates;
using PortableFormat = SomeEngine.Graphics.Format;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class D3D12MappingTests
{
    private static readonly (PortableFormat Portable, NativeFormat Native)[] FormatTable =
    [
        (PortableFormat.R8UNorm, NativeFormat.FormatR8Unorm),
        (PortableFormat.R8SNorm, NativeFormat.FormatR8SNorm),
        (PortableFormat.R8UInt, NativeFormat.FormatR8Uint),
        (PortableFormat.R8SInt, NativeFormat.FormatR8Sint),
        (PortableFormat.R8G8UNorm, NativeFormat.FormatR8G8Unorm),
        (PortableFormat.R8G8SNorm, NativeFormat.FormatR8G8SNorm),
        (PortableFormat.R8G8UInt, NativeFormat.FormatR8G8Uint),
        (PortableFormat.R8G8SInt, NativeFormat.FormatR8G8Sint),
        (PortableFormat.R8G8B8A8UNorm, NativeFormat.FormatR8G8B8A8Unorm),
        (PortableFormat.R8G8B8A8UNormSrgb, NativeFormat.FormatR8G8B8A8UnormSrgb),
        (PortableFormat.R8G8B8A8SNorm, NativeFormat.FormatR8G8B8A8SNorm),
        (PortableFormat.R8G8B8A8UInt, NativeFormat.FormatR8G8B8A8Uint),
        (PortableFormat.R8G8B8A8SInt, NativeFormat.FormatR8G8B8A8Sint),
        (PortableFormat.B8G8R8A8UNorm, NativeFormat.FormatB8G8R8A8Unorm),
        (PortableFormat.B8G8R8A8UNormSrgb, NativeFormat.FormatB8G8R8A8UnormSrgb),
        (PortableFormat.R10G10B10A2UNorm, NativeFormat.FormatR10G10B10A2Unorm),
        (PortableFormat.R11G11B10Float, NativeFormat.FormatR11G11B10Float),
        (PortableFormat.R16UNorm, NativeFormat.FormatR16Unorm),
        (PortableFormat.R16SNorm, NativeFormat.FormatR16SNorm),
        (PortableFormat.R16UInt, NativeFormat.FormatR16Uint),
        (PortableFormat.R16SInt, NativeFormat.FormatR16Sint),
        (PortableFormat.R16Float, NativeFormat.FormatR16Float),
        (PortableFormat.R16G16UNorm, NativeFormat.FormatR16G16Unorm),
        (PortableFormat.R16G16SNorm, NativeFormat.FormatR16G16SNorm),
        (PortableFormat.R16G16UInt, NativeFormat.FormatR16G16Uint),
        (PortableFormat.R16G16SInt, NativeFormat.FormatR16G16Sint),
        (PortableFormat.R16G16Float, NativeFormat.FormatR16G16Float),
        (PortableFormat.R16G16B16A16UNorm, NativeFormat.FormatR16G16B16A16Unorm),
        (PortableFormat.R16G16B16A16SNorm, NativeFormat.FormatR16G16B16A16SNorm),
        (PortableFormat.R16G16B16A16UInt, NativeFormat.FormatR16G16B16A16Uint),
        (PortableFormat.R16G16B16A16SInt, NativeFormat.FormatR16G16B16A16Sint),
        (PortableFormat.R16G16B16A16Float, NativeFormat.FormatR16G16B16A16Float),
        (PortableFormat.R32UInt, NativeFormat.FormatR32Uint),
        (PortableFormat.R32SInt, NativeFormat.FormatR32Sint),
        (PortableFormat.R32Float, NativeFormat.FormatR32Float),
        (PortableFormat.R32G32UInt, NativeFormat.FormatR32G32Uint),
        (PortableFormat.R32G32SInt, NativeFormat.FormatR32G32Sint),
        (PortableFormat.R32G32Float, NativeFormat.FormatR32G32Float),
        (PortableFormat.R32G32B32Float, NativeFormat.FormatR32G32B32Float),
        (PortableFormat.R32G32B32A32UInt, NativeFormat.FormatR32G32B32A32Uint),
        (PortableFormat.R32G32B32A32SInt, NativeFormat.FormatR32G32B32A32Sint),
        (PortableFormat.R32G32B32A32Float, NativeFormat.FormatR32G32B32A32Float),
        (PortableFormat.D16UNorm, NativeFormat.FormatD16Unorm),
        (PortableFormat.D24UNormS8UInt, NativeFormat.FormatD24UnormS8Uint),
        (PortableFormat.D32Float, NativeFormat.FormatD32Float),
        (PortableFormat.D32FloatS8UInt, NativeFormat.FormatD32FloatS8X24Uint),
        (PortableFormat.BC1UNorm, NativeFormat.FormatBC1Unorm),
        (PortableFormat.BC1UNormSrgb, NativeFormat.FormatBC1UnormSrgb),
        (PortableFormat.BC2UNorm, NativeFormat.FormatBC2Unorm),
        (PortableFormat.BC2UNormSrgb, NativeFormat.FormatBC2UnormSrgb),
        (PortableFormat.BC3UNorm, NativeFormat.FormatBC3Unorm),
        (PortableFormat.BC3UNormSrgb, NativeFormat.FormatBC3UnormSrgb),
        (PortableFormat.BC4UNorm, NativeFormat.FormatBC4Unorm),
        (PortableFormat.BC4SNorm, NativeFormat.FormatBC4SNorm),
        (PortableFormat.BC5UNorm, NativeFormat.FormatBC5Unorm),
        (PortableFormat.BC5SNorm, NativeFormat.FormatBC5SNorm),
        (PortableFormat.BC6HUFloat, NativeFormat.FormatBC6HUF16),
        (PortableFormat.BC6HSFloat, NativeFormat.FormatBC6HSF16),
        (PortableFormat.BC7UNorm, NativeFormat.FormatBC7Unorm),
        (PortableFormat.BC7UNormSrgb, NativeFormat.FormatBC7UnormSrgb),
    ];

    private static readonly (PipelineSync Portable, NativeBarrierSync Native)[] SyncTable =
    [
        (PipelineSync.None, NativeBarrierSync.None),
        (PipelineSync.Draw, NativeBarrierSync.Draw),
        (PipelineSync.IndexInput, NativeBarrierSync.IndexInput),
        (PipelineSync.VertexShading, NativeBarrierSync.VertexShading),
        (PipelineSync.PixelShading, NativeBarrierSync.PixelShading),
        (PipelineSync.DepthStencil, NativeBarrierSync.DepthStencil),
        (PipelineSync.RenderTarget, NativeBarrierSync.RenderTarget),
        (PipelineSync.ComputeShading, NativeBarrierSync.ComputeShading),
        (PipelineSync.RayTracing, NativeBarrierSync.Raytracing),
        (PipelineSync.Copy, NativeBarrierSync.Copy),
        (PipelineSync.Resolve, NativeBarrierSync.Resolve),
        (PipelineSync.ExecuteIndirect, NativeBarrierSync.ExecuteIndirect),
        (PipelineSync.Predication, NativeBarrierSync.Predication),
        (PipelineSync.AllShading, NativeBarrierSync.AllShading),
        (PipelineSync.NonPixelShading, NativeBarrierSync.NonPixelShading),
        (PipelineSync.Clear, NativeBarrierSync.ClearUnorderedAccessView),
        (PipelineSync.AccelerationStructureCopy,
            NativeBarrierSync.CopyRaytracingAccelerationStructure),
        (PipelineSync.EmitAccelerationStructurePostBuildInfo,
            NativeBarrierSync.EmitRaytracingAccelerationStructurePostbuildInfo),
        (PipelineSync.BuildRayTracingAccelerationStructure,
            NativeBarrierSync.BuildRaytracingAccelerationStructure),
        (PipelineSync.CopyRayTracingAccelerationStructure,
            NativeBarrierSync.CopyRaytracingAccelerationStructure),
        (PipelineSync.Split, NativeBarrierSync.Split),
        (PipelineSync.All, NativeBarrierSync.All),
    ];

    private static readonly (ResourceAccess Portable, NativeBarrierAccess Native)[] AccessTable =
    [
        (ResourceAccess.NoAccess, NativeBarrierAccess.NoAccess),
        (ResourceAccess.Common, NativeBarrierAccess.Common),
        (ResourceAccess.VertexBuffer, NativeBarrierAccess.VertexBuffer),
        (ResourceAccess.ConstantBuffer, NativeBarrierAccess.ConstantBuffer),
        (ResourceAccess.IndexBuffer, NativeBarrierAccess.IndexBuffer),
        (ResourceAccess.RenderTarget, NativeBarrierAccess.RenderTarget),
        (ResourceAccess.UnorderedAccess, NativeBarrierAccess.UnorderedAccess),
        (ResourceAccess.DepthStencilWrite, NativeBarrierAccess.DepthStencilWrite),
        (ResourceAccess.DepthStencilRead, NativeBarrierAccess.DepthStencilRead),
        (ResourceAccess.ShaderResource, NativeBarrierAccess.ShaderResource),
        (ResourceAccess.StreamOutput, NativeBarrierAccess.StreamOutput),
        (ResourceAccess.IndirectArgument, NativeBarrierAccess.IndirectArgument),
        (ResourceAccess.Predication, NativeBarrierAccess.Predication),
        (ResourceAccess.CopyDestination, NativeBarrierAccess.CopyDest),
        (ResourceAccess.CopySource, NativeBarrierAccess.CopySource),
        (ResourceAccess.ResolveDestination, NativeBarrierAccess.ResolveDest),
        (ResourceAccess.ResolveSource, NativeBarrierAccess.ResolveSource),
        (ResourceAccess.RayTracingAccelerationStructureRead,
            NativeBarrierAccess.RaytracingAccelerationStructureRead),
        (ResourceAccess.RayTracingAccelerationStructureWrite,
            NativeBarrierAccess.RaytracingAccelerationStructureWrite),
        (ResourceAccess.ShadingRateSource, NativeBarrierAccess.ShadingRateSource),
    ];

    private static readonly (TextureLayout Portable, NativeBarrierLayout Native)[] LayoutTable =
    [
        (TextureLayout.Undefined, NativeBarrierLayout.Undefined),
        (TextureLayout.Common, NativeBarrierLayout.Common),
        (TextureLayout.Present, NativeBarrierLayout.Present),
        (TextureLayout.RenderTarget, NativeBarrierLayout.RenderTarget),
        (TextureLayout.UnorderedAccess, NativeBarrierLayout.UnorderedAccess),
        (TextureLayout.DepthStencilWrite, NativeBarrierLayout.DepthStencilWrite),
        (TextureLayout.DepthStencilRead, NativeBarrierLayout.DepthStencilRead),
        (TextureLayout.ShaderResource, NativeBarrierLayout.ShaderResource),
        (TextureLayout.CopySource, NativeBarrierLayout.CopySource),
        (TextureLayout.CopyDestination, NativeBarrierLayout.CopyDest),
        (TextureLayout.ResolveSource, NativeBarrierLayout.ResolveSource),
        (TextureLayout.ResolveDestination, NativeBarrierLayout.ResolveDest),
        (TextureLayout.ShadingRateSource, NativeBarrierLayout.ShadingRateSource),
        (TextureLayout.QueueCommon, NativeBarrierLayout.Common),
    ];

    public static IEnumerable<object[]> FormatCases() =>
        FormatTable.Select(static item => new object[] { item.Portable, item.Native });

    public static IEnumerable<object[]> SyncCases() =>
        SyncTable.Select(static item => new object[] { item.Portable, item.Native });

    public static IEnumerable<object[]> AccessCases() =>
        AccessTable.Select(static item => new object[] { item.Portable, item.Native });

    public static IEnumerable<object[]> LayoutCases() =>
        LayoutTable.Select(static item => new object[] { item.Portable, item.Native });

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Every_portable_format_has_an_exact_DXGI_mapping(
        PortableFormat portable,
        NativeFormat expected) =>
        Assert.Equal(expected, FormatMappings.ToDxgi(portable));

    [Theory]
    [MemberData(nameof(SyncCases))]
    public void Every_pipeline_sync_value_has_an_exact_barrier_mapping(
        PipelineSync portable,
        NativeBarrierSync expected) =>
        Assert.Equal(expected, D3D12Backend.ToBarrierSync(portable));

    [Theory]
    [MemberData(nameof(AccessCases))]
    public void Every_resource_access_value_has_an_exact_barrier_mapping(
        ResourceAccess portable,
        NativeBarrierAccess expected) =>
        Assert.Equal(expected, D3D12Backend.ToBarrierAccess(portable));

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void Every_texture_layout_value_has_an_exact_barrier_mapping(
        TextureLayout portable,
        NativeBarrierLayout expected) =>
        Assert.Equal(expected, D3D12Backend.ToBarrierLayout(portable));

    [Fact]
    public void Mapping_tables_cover_each_named_portable_value_once()
    {
        Assert.Equal(Enum.GetValues<PortableFormat>(), FormatTable.Select(static x => x.Portable));
        Assert.Equal(Enum.GetValues<PipelineSync>(), SyncTable.Select(static x => x.Portable));
        Assert.Equal(Enum.GetValues<ResourceAccess>(), AccessTable.Select(static x => x.Portable));
        Assert.Equal(Enum.GetValues<TextureLayout>(), LayoutTable.Select(static x => x.Portable));
    }

    [Fact]
    public void Flag_combinations_preserve_each_native_bit()
    {
        Assert.Equal(
            NativeBarrierSync.Copy | NativeBarrierSync.ComputeShading,
            D3D12Backend.ToBarrierSync(PipelineSync.Copy | PipelineSync.ComputeShading));
        Assert.Equal(
            NativeBarrierAccess.CopySource | NativeBarrierAccess.ShaderResource,
            D3D12Backend.ToBarrierAccess(
                ResourceAccess.CopySource | ResourceAccess.ShaderResource));
        Assert.NotEqual(NativeBarrierAccess.Common, NativeBarrierAccess.NoAccess);
    }

    [Fact]
    public void Legacy_shader_resource_states_follow_the_exact_command_queue_scope()
    {
        Assert.Equal(
            NativeResourceStates.PixelShaderResource,
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Graphics,
                PipelineSync.PixelShading));
        Assert.Equal(
            NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Graphics,
                PipelineSync.VertexShading));
        Assert.Equal(
            NativeResourceStates.PixelShaderResource |
                NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Graphics,
                PipelineSync.Draw));
        Assert.Equal(
            NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Compute,
                PipelineSync.ComputeShading));
        Assert.Equal(
            NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Compute,
                PipelineSync.All));
        Assert.Throws<InvalidOperationException>(() =>
            D3D12Backend.ToLegacyShaderResourceState(
                QueueType.Copy,
                PipelineSync.Copy));
    }

    [Fact]
    public void Legacy_composite_access_uses_stage_specific_shader_resource_state()
    {
        Assert.Equal(
            NativeResourceStates.CopySource |
                NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyState(
                QueueType.Compute,
                PipelineSync.ComputeShading | PipelineSync.Copy,
                ResourceAccess.ShaderResource | ResourceAccess.CopySource));
        Assert.Equal(
            NativeResourceStates.NonPixelShaderResource,
            D3D12Backend.ToLegacyState(
                QueueType.Compute,
                PipelineSync.ComputeShading,
                TextureLayout.ShaderResource,
                ResourceAccess.ShaderResource));
    }

    [Fact]
    public void Unknown_portable_values_are_rejected()
    {
        PortableFormat unknownFormat = (PortableFormat)ushort.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatMappings.ToDxgi(unknownFormat));
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatMappings.IsDepthStencil(unknownFormat));
        Assert.Throws<ArgumentOutOfRangeException>(() => FormatMappings.PlaneCount(unknownFormat));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormatMappings.PlaneIndex(unknownFormat, TextureAspects.Color));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormatMappings.ToResourceFormat(PortableFormat.R8UNorm, [unknownFormat]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12Backend.ToBarrierSync((PipelineSync)(1UL << 63)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12Backend.ToBarrierAccess((ResourceAccess)(1UL << 63)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            D3D12Backend.ToBarrierLayout((TextureLayout)byte.MaxValue));
    }

    [Fact]
    public void Depth_stencil_format_family_has_exact_view_and_plane_mappings()
    {
        Assert.Equal(NativeFormat.FormatR16Typeless,
            FormatMappings.ToResourceFormat(PortableFormat.D16UNorm, []));
        Assert.Equal(NativeFormat.FormatR24G8Typeless,
            FormatMappings.ToResourceFormat(PortableFormat.D24UNormS8UInt, []));
        Assert.Equal(NativeFormat.FormatR32Typeless,
            FormatMappings.ToResourceFormat(PortableFormat.D32Float, []));
        Assert.Equal(NativeFormat.FormatR32G8X24Typeless,
            FormatMappings.ToResourceFormat(PortableFormat.D32FloatS8UInt, []));

        Assert.Equal(NativeFormat.FormatR16Unorm,
            FormatMappings.ToShaderViewFormat(PortableFormat.D16UNorm, TextureAspects.Depth));
        Assert.Equal(NativeFormat.FormatR24UnormX8Typeless,
            FormatMappings.ToShaderViewFormat(
                PortableFormat.D24UNormS8UInt,
                TextureAspects.Depth));
        Assert.Equal(NativeFormat.FormatX24TypelessG8Uint,
            FormatMappings.ToShaderViewFormat(
                PortableFormat.D24UNormS8UInt,
                TextureAspects.Stencil));
        Assert.Equal(NativeFormat.FormatR32Float,
            FormatMappings.ToShaderViewFormat(PortableFormat.D32Float, TextureAspects.Depth));
        Assert.Equal(NativeFormat.FormatR32FloatX8X24Typeless,
            FormatMappings.ToShaderViewFormat(
                PortableFormat.D32FloatS8UInt,
                TextureAspects.Depth));
        Assert.Equal(NativeFormat.FormatX32TypelessG8X24Uint,
            FormatMappings.ToShaderViewFormat(
                PortableFormat.D32FloatS8UInt,
                TextureAspects.Stencil));

        Assert.Equal(0u, FormatMappings.PlaneIndex(PortableFormat.D16UNorm, TextureAspects.Depth));
        Assert.Equal(0u, FormatMappings.PlaneIndex(PortableFormat.D32Float, TextureAspects.Depth));
        Assert.Equal(0u,
            FormatMappings.PlaneIndex(PortableFormat.D24UNormS8UInt, TextureAspects.Depth));
        Assert.Equal(1u,
            FormatMappings.PlaneIndex(PortableFormat.D24UNormS8UInt, TextureAspects.Stencil));
        Assert.Equal(1u, FormatMappings.PlaneCount(PortableFormat.R8UNorm));
        Assert.Equal(1u, FormatMappings.PlaneCount(PortableFormat.D32Float));
        Assert.Equal(2u, FormatMappings.PlaneCount(PortableFormat.D24UNormS8UInt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormatMappings.PlaneIndex(PortableFormat.R8UNorm, TextureAspects.Plane1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FormatMappings.PlaneIndex(PortableFormat.D32Float, TextureAspects.Stencil));
    }

    [Fact]
    public void Combined_depth_stencil_range_expands_in_plane_order()
    {
        var info = new TextureInfo(
            TextureDimension.Texture2D,
            64,
            64,
            1,
            1,
            1,
            1,
            PortableFormat.D24UNormS8UInt,
            TextureUsages.DepthStencilAttachment,
            MemoryType.DeviceLocal,
            [],
            0,
            0);
        TextureSubresourceRange range = new(
            0,
            1,
            0,
            1,
            TextureAspects.Depth | TextureAspects.Stencil);
        Span<TextureAspects> expanded = stackalloc TextureAspects[3];

        int count = D3D12Backend.ExpandBarrierAspects(info, range, expanded);

        Assert.Equal(2, count);
        Assert.Equal(TextureAspects.Depth, expanded[0]);
        Assert.Equal(TextureAspects.Stencil, expanded[1]);
    }

    [Fact]
    public void Warp_accepts_combined_depth_stencil_first_use_barrier()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                PortableFormat.D24UNormS8UInt,
                TextureUsages.DepthStencilAttachment));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange range = new(
            0,
            1,
            0,
            1,
            TextureAspects.Depth | TextureAspects.Stencil);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.None,
            PipelineSync.DepthStencil,
            ResourceAccess.NoAccess,
            ResourceAccess.DepthStencilWrite,
            TextureLayout.Undefined,
            TextureLayout.DepthStencilWrite));
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }
}
