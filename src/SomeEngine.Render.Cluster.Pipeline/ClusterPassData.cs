using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterTraversalSeedUniforms
{
    internal uint ViewCount;
    internal uint ViewIndexMode;
    internal uint MaxNodes;
    internal uint ArgsOffset;
    internal uint UseInstanceGroups;
    internal uint InvocationCountIndex;
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

internal readonly record struct ClusterIndirectDispatchParameters(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset)
{
    internal static void Declare(
        ref PassDefinition pass,
        ref ClusterIndirectDispatchParameters data) =>
        _ = pass.Read(
            data.Arguments,
            new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
            PipelineSync.ExecuteIndirect,
            ResourceAccess.IndirectArgument);

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

// Matches the records written by cluster_bin_reset.slang. These arguments are
// rebuilt with the raster bins; the graph and its consumers remain installed.
internal static class ClusterRasterArguments
{
    internal const ulong VisibilityResolveOffset = 16;
    internal const ulong HiZFirstOffset = 32;
    internal const ulong HiZTailOffset = 44;
    internal const ulong RasterScatterOffset = 56;
    internal const ulong HiZPairsOffset = 68;

    internal static ulong HiZPairOffset(int mip) =>
        HiZPairsOffset + checked((ulong)mip * ClusterIndirectAbi.DispatchBytes);
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

internal readonly record struct ClusterContinuationResetParameters(
    GraphBufferId CandidateCount,
    GraphBufferId TraversalArguments0,
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
        Buffer drawArgs = commands.GetBuffer(data.DrawArgs);
        Buffer phaseTwoCandidateCount = commands.GetBuffer(data.PhaseTwoCandidateCount);
        Buffer phaseTwoCandidateArgs = commands.GetBuffer(data.PhaseTwoCandidateArgs);
        Buffer postDrawArgs = commands.GetBuffer(data.PostDrawArgs);
        commands.ClearBuffer(candidateCount, new BufferRange(0, sizeof(uint)));
        commands.ClearBuffer(traversal0, new BufferRange(0, traversal0.Info.Size));
        commands.ClearBuffer(drawArgs, new BufferRange(0, drawArgs.Info.Size));
        commands.ClearBuffer(
            phaseTwoCandidateCount,
            new BufferRange(0, phaseTwoCandidateCount.Info.Size));
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(data.CandidateArgsInitialization), 0,
            phaseTwoCandidateArgs, 0, 16));
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
        Write(ref access, data.DrawArgs, new BufferRange(0, 16));
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterDeferredResumeResetParameters data)
    {
        Clear(ref commands, data.CandidateCount);
        Clear(ref commands, data.TraversalArguments0);
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

internal readonly record struct ClusterCandidateMetricsReadbackParameters(
    GraphBufferId CandidateCount,
    GraphBufferId CandidateArgs,
    GraphBufferId Phase2CandidateCount,
    GraphBufferId Phase2CandidateArgs,
    GraphBufferId Destination)
{
    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterCandidateMetricsReadbackParameters parameters)
    {
        Buffer destination = commands.GetBuffer(parameters.Destination);
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(parameters.CandidateCount), 0,
            destination, ClusterPipelineSystem.CandidateCountReadbackOffset, sizeof(uint)));
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(parameters.CandidateArgs), 0,
            destination, ClusterPipelineSystem.CandidateArgsReadbackOffset, 12));
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(parameters.Phase2CandidateCount), 0,
            destination, ClusterPipelineSystem.Phase2CandidateCountReadbackOffset, sizeof(uint)));
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(parameters.Phase2CandidateArgs), 0,
            destination, ClusterPipelineSystem.Phase2CandidateArgsReadbackOffset, 12));
    }
}

internal struct ClusterFrameMetricsReadbackParameters
{
    internal GraphBufferId DrawArgs;
    internal GraphBufferId Phase2DrawArgs;
    internal GraphBufferId RasterReserve;
    internal GraphBufferId ShadeCount;
    internal GraphBufferId ShadeReserve;
    internal GraphBufferId SoftwareDebug;
    internal GraphBufferId PhaseOneTessellationCounters;
    internal GraphBufferId PhaseTwoTessellationCounters;
    internal GraphBufferId WorkStatus;
    internal GraphTextureId Visibility;
    internal GraphBufferId Destination;

    internal static void Record(
        ref CopyPassCommandScope commands,
        in ClusterFrameMetricsReadbackParameters parameters)
    {
        Buffer destination = commands.GetBuffer(parameters.Destination);
        Copy(ref commands, parameters.DrawArgs, 0,
            destination, ClusterPipelineSystem.DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.Phase2DrawArgs, 0,
            destination, ClusterPipelineSystem.Phase2DrawArgsReadbackOffset, 16);
        Copy(ref commands, parameters.RasterReserve, 2 * sizeof(uint),
            destination, ClusterPipelineSystem.RasterReserveReadbackOffset,
            2 * sizeof(uint));
        Copy(ref commands, parameters.ShadeCount, 0,
            destination, ClusterPipelineSystem.ShadeBinCountReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.ShadeReserve, 0,
            destination, ClusterPipelineSystem.ShadeReserveReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.SoftwareDebug, 0,
            destination, ClusterPipelineSystem.SoftwareDebugReadbackOffset,
            sizeof(uint));
        Copy(ref commands, parameters.PhaseOneTessellationCounters, 0,
            destination, ClusterPipelineSystem.PhaseOneTessellationCountersReadbackOffset,
            8 * sizeof(uint));
        Copy(ref commands, parameters.PhaseTwoTessellationCounters, 0,
            destination, ClusterPipelineSystem.PhaseTwoTessellationCountersReadbackOffset,
            8 * sizeof(uint));
        Copy(ref commands, parameters.WorkStatus, 0,
            destination, ClusterPipelineSystem.WorkMetricsReadbackOffset,
            ClusterPipelineSystem.WorkMetricsByteSize);
        Texture visibility = commands.GetTexture(parameters.Visibility);
        uint probeX = visibility.Info.Width > ClusterPipelineSystem.VisibilityProbePixelCount
            ? (visibility.Info.Width - ClusterPipelineSystem.VisibilityProbePixelCount) / 2u
            : 0;
        commands.CopyTextureToBuffer(new BufferTextureCopy(
            destination,
            ClusterPipelineSystem.VisibilityProbeReadbackOffset,
            ClusterPipelineSystem.VisibilityProbeRowPitch,
            1,
            visibility,
            0,
            0,
            TextureAspects.Color,
            probeX,
            visibility.Info.Height / 2u,
            0,
            Math.Min(ClusterPipelineSystem.VisibilityProbePixelCount, visibility.Info.Width - probeX),
            1,
            1));
    }

    private static void Copy(
        ref CopyPassCommandScope commands,
        GraphBufferId sourceId,
        ulong sourceOffset,
        Buffer destination,
        ulong destinationOffset,
        ulong byteCount)
    {
        Buffer source = commands.GetBuffer(sourceId);
        commands.CopyBuffer(new BufferCopy(
            source,
            sourceOffset,
            destination,
            destinationOffset,
            byteCount));
    }
}
