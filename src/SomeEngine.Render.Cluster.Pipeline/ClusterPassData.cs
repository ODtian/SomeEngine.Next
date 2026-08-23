using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterDispatchParameters(DispatchArguments Dispatch)
{
    internal static void Record(
        ref ComputePassCommandScope commands,
        in ClusterDispatchParameters parameters) =>
        commands.Dispatch(parameters.Dispatch);
}

internal readonly record struct ClusterIndirectDispatchParameters(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset)
{
    internal static void Record(
        ref ComputePassCommandScope commands,
        in ClusterIndirectDispatchParameters parameters)
    {
        Buffer arguments = commands.GetBuffer(parameters.Arguments);
        commands.ExecuteIndirect(
            parameters.Layout,
            new BufferRegion(
                arguments,
                new BufferRange(parameters.Offset, ClusterIndirectAbi.DispatchBytes)),
            1);
    }
}

internal readonly record struct ClusterFullscreenParameters(int Width, int Height)
{
    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterFullscreenParameters parameters)
    {
        commands.SetViewports(
            [new Viewport(0, 0, parameters.Width, parameters.Height)]);
        commands.SetScissors(
            [new ScissorRect(0, 0, parameters.Width, parameters.Height)]);
        commands.Draw(new DrawArguments(3, 1, 0, 0));
    }
}

internal readonly record struct ClusterIndirectDrawParameters(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset,
    int Width,
    int Height)
{
    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterIndirectDrawParameters parameters)
    {
        commands.SetViewports(
            [new Viewport(0, 0, parameters.Width, parameters.Height)]);
        commands.SetScissors(
            [new ScissorRect(0, 0, parameters.Width, parameters.Height)]);
        Buffer arguments = commands.GetBuffer(parameters.Arguments);
        commands.ExecuteIndirect(
            parameters.Layout,
            new BufferRegion(
                arguments,
                new BufferRange(parameters.Offset, ClusterIndirectAbi.DrawBytes)),
            1);
    }
}

internal readonly record struct ClusterBufferClearParameters(GraphBufferId Buffer)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterBufferClearParameters data)
    {
        Buffer buffer = commands.GetBuffer(data.Buffer);
        commands.ClearBuffer(buffer, new BufferRange(0, buffer.Info.Size));
    }
}

internal readonly record struct ClusterColorClearParameters(
    GraphTextureId Texture,
    TextureSubresourceRange Range,
    Vector4 Color)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterColorClearParameters data) =>
        commands.ClearTexture(
            commands.GetTexture(data.Texture),
            data.Range,
            data.Color);
}

internal readonly record struct ClusterDepthClearParameters(
    GraphTextureId Texture,
    TextureSubresourceRange Range,
    float Depth,
    byte Stencil)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterDepthClearParameters data) =>
        commands.ClearDepthStencil(
            commands.GetTexture(data.Texture),
            data.Range,
            data.Depth,
            data.Stencil);
}

internal readonly record struct ClusterTargetClearParameters(
    GraphColorAttachmentViewId Visibility,
    GraphColorAttachmentViewId SoftwareDepth,
    GraphColorAttachmentViewId SceneColor,
    GraphColorAttachmentViewId MotionVectors,
    GraphDepthStencilViewId Depth)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterTargetClearParameters data)
    {
        access.ColorAttachment(
            0,
            data.Visibility,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            Vector4.Zero);
        access.ColorAttachment(
            1,
            data.SoftwareDepth,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            Vector4.Zero);
        access.ColorAttachment(
            2,
            data.SceneColor,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            new Vector4(0, 0, 0, 1));
        access.ColorAttachment(
            3,
            data.MotionVectors,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            Vector4.Zero);
        access.DepthStencilAttachment(
            data.Depth,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            1,
            LoadType.Discard,
            StoreType.Discard,
            WriteCoverage.Partial,
            0);
    }

    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterTargetClearParameters data)
    {
    }
}

internal readonly record struct ClusterColorAttachmentClearParameters(
    GraphColorAttachmentViewId View,
    Vector4 Color)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterColorAttachmentClearParameters data) =>
        access.ColorAttachment(
            0,
            data.View,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            data.Color);

    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterColorAttachmentClearParameters data)
    {
    }
}

internal readonly record struct ClusterDepthAttachmentClearParameters(
    GraphDepthStencilViewId View)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterDepthAttachmentClearParameters data) =>
        access.DepthStencilAttachment(
            data.View,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            1,
            LoadType.Discard,
            StoreType.Discard,
            WriteCoverage.Partial,
            0);

    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterDepthAttachmentClearParameters data)
    {
    }
}

internal readonly record struct ClusterHistoryCopyParameters(
    GraphTextureId SceneSource,
    GraphTextureId SceneDestination,
    GraphTextureId MotionSource,
    GraphTextureId MotionDestination,
    GraphTextureId DepthSource,
    GraphTextureId DepthDestination,
    int Width,
    int Height)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterHistoryCopyParameters parameters)
    {
        Copy(
            ref commands,
            parameters.SceneSource,
            parameters.SceneDestination,
            parameters.Width,
            parameters.Height,
            TextureAspects.Color);
        Copy(
            ref commands,
            parameters.MotionSource,
            parameters.MotionDestination,
            parameters.Width,
            parameters.Height,
            TextureAspects.Color);
        Copy(
            ref commands,
            parameters.DepthSource,
            parameters.DepthDestination,
            parameters.Width,
            parameters.Height,
            TextureAspects.Depth);
    }

    private static void Copy(
        ref CopyPassCommandScope commands,
        GraphTextureId sourceId,
        GraphTextureId destinationId,
        int width,
        int height,
        TextureAspects aspect)
    {
        Texture source = commands.GetTexture(sourceId);
        Texture destination = commands.GetTexture(destinationId);
        commands.CopyTexture(new TextureCopy(
            source,
            0,
            0,
            aspect,
            0,
            0,
            0,
            destination,
            0,
            0,
            aspect,
            0,
            0,
            0,
            checked((uint)width),
            checked((uint)height),
            1));
    }
}

internal readonly record struct ClusterBufferCopyParameters(
    GraphBufferId Source,
    GraphBufferId Destination,
    ulong ByteCount)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterBufferCopyParameters parameters)
    {
        Buffer source = commands.GetBuffer(parameters.Source);
        Buffer destination = commands.GetBuffer(parameters.Destination);
        commands.CopyBuffer(new BufferCopy(
            source,
            0,
            destination,
            0,
            parameters.ByteCount));
    }
}

internal struct ClusterFrameMetricsReadbackParameters
{
    internal GraphBufferId CandidateCount;
    internal GraphBufferId CandidateArgs;
    internal GraphBufferId DrawArgs;
    internal GraphBufferId Phase2CandidateCount;
    internal GraphBufferId Phase2CandidateArgs;
    internal GraphBufferId Phase2DrawArgs;
    internal GraphBufferId RasterReserve;
    internal GraphBufferId ShadeReserve;
    internal GraphBufferId DeformReserve;
    internal GraphBufferId CacheAllocation;
    internal GraphBufferId SoftwareDebug;
    internal GraphBufferId Destination;

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterFrameMetricsReadbackParameters parameters)
    {
        Copy(ref commands, parameters.CandidateCount, 0,
            parameters.Destination, ClusterRendererSystem.CandidateCountReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.CandidateArgs, 0,
            parameters.Destination, ClusterRendererSystem.CandidateArgsReadbackOffset, 12);
        Copy(ref commands, parameters.DrawArgs, 0,
            parameters.Destination, ClusterRendererSystem.DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.Phase2CandidateCount, 0,
            parameters.Destination, ClusterRendererSystem.Phase2CandidateCountReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.Phase2CandidateArgs, 0,
            parameters.Destination, ClusterRendererSystem.Phase2CandidateArgsReadbackOffset, 12);
        Copy(ref commands, parameters.Phase2DrawArgs, 0,
            parameters.Destination, ClusterRendererSystem.Phase2DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.RasterReserve, 2 * sizeof(uint),
            parameters.Destination, ClusterRendererSystem.RasterReserveReadbackOffset,
            2 * sizeof(uint));
        Copy(ref commands, parameters.ShadeReserve, 0,
            parameters.Destination, ClusterRendererSystem.ShadeReserveReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.DeformReserve, sizeof(uint),
            parameters.Destination, ClusterRendererSystem.DeformReserveReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.CacheAllocation, 0,
            parameters.Destination, ClusterRendererSystem.CacheAllocationReadbackOffset,
            2 * sizeof(uint));
        Copy(ref commands, parameters.SoftwareDebug, 0,
            parameters.Destination, ClusterRendererSystem.SoftwareDebugReadbackOffset,
            sizeof(uint));
    }

    private static void Copy(
        ref CopyPassCommandScope commands,
        GraphBufferId sourceId,
        ulong sourceOffset,
        GraphBufferId destinationId,
        ulong destinationOffset,
        ulong byteCount)
    {
        Buffer source = commands.GetBuffer(sourceId);
        Buffer destination = commands.GetBuffer(destinationId);
        commands.CopyBuffer(new BufferCopy(
            source,
            sourceOffset,
            destination,
            destinationOffset,
            byteCount));
    }
}
