using Vortice.Direct3D12;
using DxgiFormat = Vortice.DXGI.Format;
using GraphicsBlendFactor = SomeEngine.Graphics.BlendFactor;
using GraphicsBlendOperation = SomeEngine.Graphics.BlendOperation;
using GraphicsCompareOp = SomeEngine.Graphics.CompareOp;
using GraphicsCullMode = SomeEngine.Graphics.CullMode;
using GraphicsFillMode = SomeEngine.Graphics.FillMode;
using GraphicsFormat = SomeEngine.Graphics.Format;
using GraphicsPrimitiveTopology = SomeEngine.Graphics.PrimitiveTopology;
using GraphicsResourceState = SomeEngine.Graphics.ResourceState;

namespace SomeEngine.Graphics.Direct3D12;

internal static class Mappings
{
    public static CommandListType CommandListType(QueueType queue) => queue switch
    {
        QueueType.Graphics => Vortice.Direct3D12.CommandListType.Direct,
        QueueType.Compute => Vortice.Direct3D12.CommandListType.Compute,
        QueueType.Copy => Vortice.Direct3D12.CommandListType.Copy,
        _ => throw new ArgumentOutOfRangeException(nameof(queue)),
    };

    public static DxgiFormat Format(GraphicsFormat format) => format switch
    {
        GraphicsFormat.Unknown => DxgiFormat.Unknown,
        GraphicsFormat.R8UNorm => DxgiFormat.R8_UNorm,
        GraphicsFormat.R8G8UNorm => DxgiFormat.R8G8_UNorm,
        GraphicsFormat.R8G8B8A8UNorm => DxgiFormat.R8G8B8A8_UNorm,
        GraphicsFormat.R8G8B8A8UNormSrgb => DxgiFormat.R8G8B8A8_UNorm_SRgb,
        GraphicsFormat.B8G8R8A8UNorm => DxgiFormat.B8G8R8A8_UNorm,
        GraphicsFormat.R16UInt => DxgiFormat.R16_UInt,
        GraphicsFormat.R16Float => DxgiFormat.R16_Float,
        GraphicsFormat.R16G16Float => DxgiFormat.R16G16_Float,
        GraphicsFormat.R16G16B16A16Float => DxgiFormat.R16G16B16A16_Float,
        GraphicsFormat.R32UInt => DxgiFormat.R32_UInt,
        GraphicsFormat.R32Float => DxgiFormat.R32_Float,
        GraphicsFormat.R32G32Float => DxgiFormat.R32G32_Float,
        GraphicsFormat.R32G32B32Float => DxgiFormat.R32G32B32_Float,
        GraphicsFormat.R32G32B32A32Float => DxgiFormat.R32G32B32A32_Float,
        GraphicsFormat.D24UNormS8UInt => DxgiFormat.D24_UNorm_S8_UInt,
        GraphicsFormat.D32Float => DxgiFormat.D32_Float,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static DxgiFormat ResourceFormat(GraphicsFormat format) => format switch
    {
        GraphicsFormat.D24UNormS8UInt => DxgiFormat.R24G8_Typeless,
        GraphicsFormat.D32Float => DxgiFormat.R32_Typeless,
        _ => Format(format),
    };

    public static DxgiFormat ResourceFormat(in TextureDesc desc)
    {
        bool rgba8Castable = desc.AllowedViewFormats.Contains(GraphicsFormat.R8G8B8A8UNorm) &&
                             desc.AllowedViewFormats.Contains(GraphicsFormat.R8G8B8A8UNormSrgb);
        return rgba8Castable ? DxgiFormat.R8G8B8A8_Typeless : ResourceFormat(desc.Format);
    }

    public static DxgiFormat ShaderViewFormat(GraphicsFormat format, TextureAspect aspect) => (format, aspect) switch
    {
        (GraphicsFormat.D24UNormS8UInt, TextureAspect.Depth) => DxgiFormat.R24_UNorm_X8_Typeless,
        (GraphicsFormat.D24UNormS8UInt, TextureAspect.Stencil) => DxgiFormat.X24_Typeless_G8_UInt,
        (GraphicsFormat.D32Float, TextureAspect.Depth) => DxgiFormat.R32_Float,
        (_, TextureAspect.Color) => Format(format),
        _ => throw new ArgumentException($"Format {format} does not expose shader aspect {aspect}.", nameof(aspect)),
    };

    public static ResourceStates ResourceState(GraphicsResourceState state) => state switch
    {
        GraphicsResourceState.Common => ResourceStates.Common,
        GraphicsResourceState.CopySource => ResourceStates.CopySource,
        GraphicsResourceState.CopyDestination => ResourceStates.CopyDest,
        GraphicsResourceState.ShaderResource => ResourceStates.AllShaderResource,
        GraphicsResourceState.UnorderedAccess => ResourceStates.UnorderedAccess,
        GraphicsResourceState.RenderTarget => ResourceStates.RenderTarget,
        GraphicsResourceState.DepthWrite => ResourceStates.DepthWrite,
        GraphicsResourceState.DepthRead => ResourceStates.DepthRead,
        GraphicsResourceState.VertexOrConstantBuffer => ResourceStates.VertexAndConstantBuffer,
        GraphicsResourceState.IndexBuffer => ResourceStates.IndexBuffer,
        GraphicsResourceState.IndirectArgument => ResourceStates.IndirectArgument,
        GraphicsResourceState.Present => ResourceStates.Present,
        GraphicsResourceState.ResolveSource => ResourceStates.ResolveSource,
        GraphicsResourceState.ResolveDestination => ResourceStates.ResolveDest,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static PrimitiveTopologyType TopologyType(GraphicsPrimitiveTopology topology) => topology switch
    {
        GraphicsPrimitiveTopology.PointList => PrimitiveTopologyType.Point,
        GraphicsPrimitiveTopology.LineList or GraphicsPrimitiveTopology.LineStrip => PrimitiveTopologyType.Line,
        GraphicsPrimitiveTopology.TriangleList or GraphicsPrimitiveTopology.TriangleStrip => PrimitiveTopologyType.Triangle,
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };

    public static Vortice.Direct3D.PrimitiveTopology Topology(GraphicsPrimitiveTopology topology) => topology switch
    {
        GraphicsPrimitiveTopology.PointList => Vortice.Direct3D.PrimitiveTopology.PointList,
        GraphicsPrimitiveTopology.LineList => Vortice.Direct3D.PrimitiveTopology.LineList,
        GraphicsPrimitiveTopology.LineStrip => Vortice.Direct3D.PrimitiveTopology.LineStrip,
        GraphicsPrimitiveTopology.TriangleList => Vortice.Direct3D.PrimitiveTopology.TriangleList,
        GraphicsPrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };

    public static Vortice.Direct3D12.FillMode FillMode(GraphicsFillMode value) => value switch
    {
        GraphicsFillMode.Wireframe => Vortice.Direct3D12.FillMode.Wireframe,
        _ => Vortice.Direct3D12.FillMode.Solid,
    };

    public static Vortice.Direct3D12.CullMode CullMode(GraphicsCullMode value) => value switch
    {
        GraphicsCullMode.Front => Vortice.Direct3D12.CullMode.Front,
        GraphicsCullMode.Back => Vortice.Direct3D12.CullMode.Back,
        _ => Vortice.Direct3D12.CullMode.None,
    };

    public static ComparisonFunction Comparison(GraphicsCompareOp value) => value switch
    {
        GraphicsCompareOp.Never => ComparisonFunction.Never,
        GraphicsCompareOp.Less => ComparisonFunction.Less,
        GraphicsCompareOp.Equal => ComparisonFunction.Equal,
        GraphicsCompareOp.LessOrEqual => ComparisonFunction.LessEqual,
        GraphicsCompareOp.Greater => ComparisonFunction.Greater,
        GraphicsCompareOp.NotEqual => ComparisonFunction.NotEqual,
        GraphicsCompareOp.GreaterOrEqual => ComparisonFunction.GreaterEqual,
        GraphicsCompareOp.Always => ComparisonFunction.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static Blend Blend(GraphicsBlendFactor value) => value switch
    {
        GraphicsBlendFactor.Zero => Vortice.Direct3D12.Blend.Zero,
        GraphicsBlendFactor.One => Vortice.Direct3D12.Blend.One,
        GraphicsBlendFactor.SourceColor => Vortice.Direct3D12.Blend.SourceColor,
        GraphicsBlendFactor.OneMinusSourceColor => Vortice.Direct3D12.Blend.InverseSourceColor,
        GraphicsBlendFactor.SourceAlpha => Vortice.Direct3D12.Blend.SourceAlpha,
        GraphicsBlendFactor.OneMinusSourceAlpha => Vortice.Direct3D12.Blend.InverseSourceAlpha,
        GraphicsBlendFactor.DestinationColor => Vortice.Direct3D12.Blend.DestinationColor,
        GraphicsBlendFactor.OneMinusDestinationColor => Vortice.Direct3D12.Blend.InverseDestinationColor,
        GraphicsBlendFactor.DestinationAlpha => Vortice.Direct3D12.Blend.DestinationAlpha,
        GraphicsBlendFactor.OneMinusDestinationAlpha => Vortice.Direct3D12.Blend.InverseDestinationAlpha,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static Vortice.Direct3D12.BlendOperation BlendOperation(GraphicsBlendOperation value) => value switch
    {
        GraphicsBlendOperation.Add => Vortice.Direct3D12.BlendOperation.Add,
        GraphicsBlendOperation.Subtract => Vortice.Direct3D12.BlendOperation.Subtract,
        GraphicsBlendOperation.ReverseSubtract => Vortice.Direct3D12.BlendOperation.RevSubtract,
        GraphicsBlendOperation.Minimum => Vortice.Direct3D12.BlendOperation.Min,
        GraphicsBlendOperation.Maximum => Vortice.Direct3D12.BlendOperation.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
