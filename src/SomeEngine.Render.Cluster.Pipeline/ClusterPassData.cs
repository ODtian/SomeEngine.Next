using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterDispatch(uint X, uint Y, uint Z);

internal sealed class ClusterClearPassData;

internal sealed class ClusterClearBuffersPassData
{
    internal BufferHandle[] Buffers { get; set; } = [];
}

internal sealed class ClusterBufferCopyPassData
{
    internal BufferHandle Source { get; set; }
    internal BufferHandle Destination { get; set; }
    internal ulong ByteCount { get; set; }
}

internal sealed class ClusterDispatchPassData
{
    internal ClusterDispatch Dispatch { get; set; }

    internal static void Execute(
        ClusterDispatchPassData data,
        UnsafeGraphContext context)
    {
        context.Dispatch(data.Dispatch.X, data.Dispatch.Y, data.Dispatch.Z);
    }
}

internal sealed class ClusterIndirectPassData
{
    internal IndirectCommandLayout Layout { get; set; } = null!;
    internal BufferHandle IndirectArguments { get; set; }
    internal ulong IndirectOffset { get; set; }

    internal static void Execute(
        ClusterIndirectPassData data,
        UnsafeGraphContext context)
    {
        context.ExecuteIndirect(
            data.Layout,
            data.IndirectArguments,
            data.IndirectOffset,
            ClusterIndirectAbi.DispatchBytes,
            1);
    }
}

internal sealed class ClusterFullscreenPassData
{
    internal int Width { get; set; }
    internal int Height { get; set; }

    internal static void Execute(
        ClusterFullscreenPassData data,
        UnsafeGraphContext context)
    {
        context.SetViewport(new Viewport(0, 0, data.Width, data.Height));
        context.SetScissor(new ScissorRect(0, 0, data.Width, data.Height));
        context.Draw(3);
    }
}

internal sealed class ClusterHardwareRasterPassData
{
    internal IndirectCommandLayout Layout { get; set; } = null!;
    internal BufferHandle IndirectArguments { get; set; }
    internal ulong IndirectOffset { get; set; }
    internal int Width { get; set; }
    internal int Height { get; set; }

    internal static void Execute(
        ClusterHardwareRasterPassData data,
        UnsafeGraphContext context)
    {
        context.SetViewport(new Viewport(0, 0, data.Width, data.Height));
        context.SetScissor(new ScissorRect(0, 0, data.Width, data.Height));
        context.ExecuteIndirect(
            data.Layout,
            data.IndirectArguments,
            data.IndirectOffset,
            ClusterIndirectAbi.DrawBytes,
            1);
    }
}

internal sealed class ClusterHistoryCopyPassData
{
    internal TextureHandle SceneSource { get; set; }
    internal TextureHandle SceneDestination { get; set; }
    internal TextureHandle MotionSource { get; set; }
    internal TextureHandle MotionDestination { get; set; }
    internal TextureHandle DepthSource { get; set; }
    internal TextureHandle DepthDestination { get; set; }
    internal int Width { get; set; }
    internal int Height { get; set; }

    internal static void Execute(
        ClusterHistoryCopyPassData data,
        UnsafeGraphContext context)
    {
        context.CopyTexture(
            data.SceneSource,
            data.SceneDestination,
            FullCopy(data.Width, data.Height, TextureAspects.Color));
        context.CopyTexture(
            data.MotionSource,
            data.MotionDestination,
            FullCopy(data.Width, data.Height, TextureAspects.Color));
        context.CopyTexture(
            data.DepthSource,
            data.DepthDestination,
            FullCopy(data.Width, data.Height, TextureAspects.Depth));
    }

    private static GraphTextureCopy FullCopy(
        int width,
        int height,
        TextureAspects aspect)
    {
        GraphTextureRegion region = new(
            0,
            0,
            aspect,
            0,
            0,
            0,
            checked((uint)width),
            checked((uint)height));
        return new GraphTextureCopy(region, region);
    }
}

internal sealed class ClusterDiagnosticsReadbackPassData
{
    internal BufferHandle CandidateCount { get; set; }
    internal BufferHandle CandidateArgs { get; set; }
    internal BufferHandle DrawArgs { get; set; }
    internal BufferHandle Phase2CandidateCount { get; set; }
    internal BufferHandle Phase2CandidateArgs { get; set; }
    internal BufferHandle Phase2DrawArgs { get; set; }
    internal BufferHandle RasterReserve { get; set; }
    internal BufferHandle ShadeReserve { get; set; }
    internal BufferHandle DeformReserve { get; set; }
    internal BufferHandle CacheAllocation { get; set; }
    internal BufferHandle SoftwareDebug { get; set; }
    internal BufferHandle Destination { get; set; }
}
