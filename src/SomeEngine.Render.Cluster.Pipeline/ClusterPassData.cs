using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterTraversalSeedUniforms
{
    internal uint ViewCount;
    internal uint ViewIndexMode;
    internal uint MaxNodes;
    internal uint ArgsOffset;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterTraversalLevelUniforms
{
    internal uint NodeLevel;
    internal uint MaxNodes;
    internal uint CurrentArgsOffset;
    internal uint NextArgsOffset;
    internal uint TraversalMode;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDeferredResumeUniforms
{
    internal uint MaxWorkItems;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
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

internal readonly record struct ClusterFullscreenParameters(
    int Width,
    int Height,
    uint ViewCount)
{
    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterFullscreenParameters parameters)
    {
        commands.SetViewports(
            [new Viewport(0, 0, parameters.Width, parameters.Height)]);
        commands.SetScissors(
            [new ScissorRect(0, 0, parameters.Width, parameters.Height)]);
        commands.Draw(new DrawArguments(3, parameters.ViewCount, 0, 0));
    }
}

internal readonly record struct ClusterLayerDispatch(
    GraphPassId Pass,
    uint GroupX,
    uint GroupY);

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

internal readonly record struct ClusterTessellationResetParameters(GraphBufferId Counters)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterTessellationResetParameters data)
    {
        Buffer counters = commands.GetBuffer(data.Counters);
        commands.ClearBuffer(counters, new BufferRange(0, counters.Info.Size));
    }
}

internal readonly record struct ClusterContinuationResetParameters(
    GraphBufferId CandidateCount,
    GraphBufferId TraversalArguments0,
    GraphBufferId TraversalArguments1,
    GraphBufferId CandidateArgsInitialization,
    GraphBufferId CandidateArgs,
    GraphBufferId DrawArgs,
    GraphBufferId PhaseTwoCandidateCount,
    GraphBufferId PhaseTwoCandidateArgs,
    GraphBufferId PostDrawArgs)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterContinuationResetParameters data)
    {
        _ = access.Write(
            data.CandidateCount,
            new BufferRange(0, sizeof(uint)),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.TraversalArguments0,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.TraversalArguments1,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Read(
            data.CandidateArgsInitialization,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopySource);
        _ = access.Write(
            data.CandidateArgs,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.DrawArgs,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.PhaseTwoCandidateCount,
            new BufferRange(0, sizeof(uint)),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.PhaseTwoCandidateArgs,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.PostDrawArgs,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterContinuationResetParameters data)
    {
        Buffer candidateCount = commands.GetBuffer(data.CandidateCount);
        Buffer traversal0 = commands.GetBuffer(data.TraversalArguments0);
        Buffer traversal1 = commands.GetBuffer(data.TraversalArguments1);
        Buffer drawArgs = commands.GetBuffer(data.DrawArgs);
        Buffer phaseTwoCandidateCount = commands.GetBuffer(data.PhaseTwoCandidateCount);
        Buffer phaseTwoCandidateArgs = commands.GetBuffer(data.PhaseTwoCandidateArgs);
        Buffer postDrawArgs = commands.GetBuffer(data.PostDrawArgs);
        commands.ClearBuffer(candidateCount, new BufferRange(0, sizeof(uint)));
        commands.ClearBuffer(traversal0, new BufferRange(0, traversal0.Info.Size));
        commands.ClearBuffer(traversal1, new BufferRange(0, traversal1.Info.Size));
        commands.ClearBuffer(drawArgs, new BufferRange(0, drawArgs.Info.Size));
        commands.ClearBuffer(
            phaseTwoCandidateCount,
            new BufferRange(0, phaseTwoCandidateCount.Info.Size));
        commands.ClearBuffer(
            phaseTwoCandidateArgs,
            new BufferRange(0, phaseTwoCandidateArgs.Info.Size));
        commands.ClearBuffer(postDrawArgs, new BufferRange(0, postDrawArgs.Info.Size));
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(data.CandidateArgsInitialization),
            0,
            commands.GetBuffer(data.CandidateArgs),
            0,
            16));
    }
}

internal readonly record struct ClusterDeferredResumeResetParameters(
    GraphBufferId CandidateCount,
    GraphBufferId CandidateArgsInitialization,
    GraphBufferId CandidateArgs,
    GraphBufferId TraversalArguments0,
    GraphBufferId TraversalArguments1,
    GraphBufferId DrawArgs)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterDeferredResumeResetParameters data)
    {
        Write(ref access, data.CandidateCount, new BufferRange(0, sizeof(uint)));
        _ = access.Read(
            data.CandidateArgsInitialization,
            new BufferRange(0, 16),
            PipelineSync.Copy,
            ResourceAccess.CopySource);
        Write(ref access, data.CandidateArgs, new BufferRange(0, 16));
        Write(ref access, data.TraversalArguments0, BufferRange.Whole);
        Write(ref access, data.TraversalArguments1, BufferRange.Whole);
        Write(ref access, data.DrawArgs, new BufferRange(0, 16));
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterDeferredResumeResetParameters data)
    {
        Clear(ref commands, data.CandidateCount);
        Clear(ref commands, data.TraversalArguments0);
        Clear(ref commands, data.TraversalArguments1);
        Clear(ref commands, data.DrawArgs);
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(data.CandidateArgsInitialization),
            0,
            commands.GetBuffer(data.CandidateArgs),
            0,
            16));
    }

    private static void Write(
        ref PassDefinition access,
        GraphBufferId buffer,
        in BufferRange range) =>
        _ = access.Write(
            buffer,
            range,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    private static void Clear(ref CopyPassCommandScope commands, GraphBufferId buffer)
    {
        Buffer resource = commands.GetBuffer(buffer);
        commands.ClearBuffer(resource, new BufferRange(0, resource.Info.Size));
    }
}

internal readonly record struct ClusterTessellationLevelResetParameters(
    GraphBufferId Counts,
    GraphBufferId[] DispatchArguments)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterTessellationLevelResetParameters data)
    {
        Buffer counts = commands.GetBuffer(data.Counts);
        commands.ClearBuffer(counts, new BufferRange(0, counts.Info.Size));
        foreach (GraphBufferId argumentId in data.DispatchArguments)
        {
            Buffer arguments = commands.GetBuffer(argumentId);
            commands.ClearBuffer(arguments, new BufferRange(0, arguments.Info.Size));
        }
    }
}

internal readonly record struct ClusterBufferRangeClearParameters(
    GraphBufferId Buffer,
    BufferRange Range)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterBufferRangeClearParameters data)
    {
        Buffer buffer = commands.GetBuffer(data.Buffer);
        commands.ClearBuffer(buffer, data.Range);
    }
}

internal readonly record struct ClusterFrameMetricsSourceResetParameters(
    GraphBufferId RasterReserve,
    GraphBufferId ShadeCount,
    GraphBufferId ShadeReserve,
    GraphBufferId SoftwareDebug,
    GraphBufferId PhaseOneTessellationCounters,
    GraphBufferId PhaseTwoTessellationCounters)
{
    private static readonly BufferRange RasterReserveRange =
        new(0, 4 * sizeof(uint));
    private static readonly BufferRange ScalarRange = new(0, sizeof(uint));
    private static readonly BufferRange TessellationRange = new(0, 8 * sizeof(uint));

    internal static void Declare(
        ref PassDefinition access,
        ref ClusterFrameMetricsSourceResetParameters data)
    {
        Write(ref access, data.RasterReserve, RasterReserveRange);
        Write(ref access, data.ShadeCount, ScalarRange);
        Write(ref access, data.ShadeReserve, ScalarRange);
        Write(ref access, data.SoftwareDebug, ScalarRange);
        Write(ref access, data.PhaseOneTessellationCounters, TessellationRange);
        Write(ref access, data.PhaseTwoTessellationCounters, TessellationRange);
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterFrameMetricsSourceResetParameters data)
    {
        Clear(ref commands, data.RasterReserve, RasterReserveRange);
        Clear(ref commands, data.ShadeCount, ScalarRange);
        Clear(ref commands, data.ShadeReserve, ScalarRange);
        Clear(ref commands, data.SoftwareDebug, ScalarRange);
        Clear(ref commands, data.PhaseOneTessellationCounters, TessellationRange);
        Clear(ref commands, data.PhaseTwoTessellationCounters, TessellationRange);
    }

    private static void Write(
        ref PassDefinition access,
        GraphBufferId buffer,
        in BufferRange range) =>
        _ = access.Write(
            buffer,
            range,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    private static void Clear(
        ref CopyPassCommandScope commands,
        GraphBufferId buffer,
        in BufferRange range) =>
        commands.ClearBuffer(commands.GetBuffer(buffer), range);
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
    GraphColorAttachmentViewId SceneColor,
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
            1u,
            data.SceneColor,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            new Vector4(0, 0, 0, 1));
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

internal readonly record struct ClusterPolicyTargetClearParameters(
    GraphColorAttachmentViewId Visibility,
    GraphDepthStencilViewId Depth)
{
    internal static void Declare(
        ref PassDefinition access,
        ref ClusterPolicyTargetClearParameters data)
    {
        access.ColorAttachment(
            0,
            data.Visibility,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            Vector4.Zero);
        access.DepthStencilAttachment(
            data.Depth,
            LoadType.Clear,
            StoreType.Store,
            WriteCoverage.Complete,
            1f,
            LoadType.Discard,
            StoreType.Discard,
            WriteCoverage.Complete,
            0);
    }

    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterPolicyTargetClearParameters data)
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

internal readonly record struct ClusterBufferCopyParameters(
    GraphBufferId Source,
    ulong SourceOffset,
    GraphBufferId Destination,
    ulong DestinationOffset,
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
            parameters.SourceOffset,
            destination,
            parameters.DestinationOffset,
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
    internal GraphBufferId ShadeCount;
    internal GraphBufferId ShadeReserve;
    internal GraphBufferId SoftwareDebug;
    internal GraphBufferId PhaseOneTessellationCounters;
    internal GraphBufferId PhaseTwoTessellationCounters;
    internal GraphTextureId Visibility;
    internal uint VisibilityProbeX;
    internal uint VisibilityProbeY;
    internal GraphBufferId Destination;

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterFrameMetricsReadbackParameters parameters)
    {
        Copy(ref commands, parameters.DrawArgs, 0,
            parameters.Destination, ClusterPipelineSystem.DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.Phase2DrawArgs, 0,
            parameters.Destination, ClusterPipelineSystem.Phase2DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.RasterReserve, 2 * sizeof(uint),
            parameters.Destination, ClusterPipelineSystem.RasterReserveReadbackOffset,
            2 * sizeof(uint));
        Copy(ref commands, parameters.ShadeCount, 0,
            parameters.Destination, ClusterPipelineSystem.ShadeBinCountReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.ShadeReserve, 0,
            parameters.Destination, ClusterPipelineSystem.ShadeReserveReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.SoftwareDebug, 0,
            parameters.Destination, ClusterPipelineSystem.SoftwareDebugReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.PhaseOneTessellationCounters, 0,
            parameters.Destination, ClusterPipelineSystem.PhaseOneTessellationCountersReadbackOffset,
            8 * sizeof(uint));
        Copy(ref commands, parameters.PhaseTwoTessellationCounters, 0,
            parameters.Destination, ClusterPipelineSystem.PhaseTwoTessellationCountersReadbackOffset,
            8 * sizeof(uint));
        Buffer destination = commands.GetBuffer(parameters.Destination);
        Texture visibility = commands.GetTexture(parameters.Visibility);
        commands.CopyTextureToBuffer(new BufferTextureCopy(
            destination,
            ClusterPipelineSystem.VisibilityProbeReadbackOffset,
            ClusterPipelineSystem.VisibilityProbeRowPitch,
            1,
            visibility,
            0,
            0,
            TextureAspects.Color,
            parameters.VisibilityProbeX,
            parameters.VisibilityProbeY,
            0,
            Math.Min(ClusterPipelineSystem.VisibilityProbePixelCount, visibility.Info.Width - parameters.VisibilityProbeX),
            1,
            1));
    }

    internal static void RecordCandidates(
        ref CopyPassCommandScope commands,
        in ClusterFrameMetricsReadbackParameters parameters)
    {
        Copy(ref commands, parameters.CandidateCount, 0,
            parameters.Destination, ClusterPipelineSystem.CandidateCountReadbackOffset, sizeof(uint));
        Copy(ref commands, parameters.CandidateArgs, 0,
            parameters.Destination, ClusterPipelineSystem.CandidateArgsReadbackOffset, 12);
        Copy(ref commands, parameters.Phase2CandidateCount, 0,
            parameters.Destination, ClusterPipelineSystem.Phase2CandidateCountReadbackOffset, sizeof(uint));
        Copy(ref commands, parameters.Phase2CandidateArgs, 0,
            parameters.Destination, ClusterPipelineSystem.Phase2CandidateArgsReadbackOffset, 12);
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
