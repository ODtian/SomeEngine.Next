using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Features.Shadows;

internal readonly record struct VirtualShadowAtlasInitializePassData(GraphTextureId Atlas)
{
    private static readonly TextureSubresourceRange AtlasRange =
        new(0, 1, 0, 1, TextureAspects.Color);

    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowAtlasInitializePassData data) =>
        _ = access.Write(
            data.Atlas,
            AtlasRange,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            TextureLayout.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowAtlasInitializePassData data) =>
        commands.ClearTexture(
            commands.GetTexture(data.Atlas),
            AtlasRange,
            System.Numerics.Vector4.Zero);
}

internal readonly record struct VirtualShadowPageHiZStorageInitializePassData(
    GraphTextureId PageHiZ,
    TextureSubresourceRange Range)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowPageHiZStorageInitializePassData data) =>
        _ = access.Write(
            data.PageHiZ,
            data.Range,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            TextureLayout.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowPageHiZStorageInitializePassData data) =>
        commands.ClearTexture(
            commands.GetTexture(data.PageHiZ),
            data.Range,
            System.Numerics.Vector4.One);
}

internal readonly record struct VirtualShadowDispatchPassData(DispatchArguments Dispatch)
{
    internal static void Record(
        ref ComputePassCommandScope commands,
        in VirtualShadowDispatchPassData data) =>
        commands.Dispatch(data.Dispatch);
}

internal readonly record struct VirtualShadowIndirectPassData(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset)
{
    private const ulong DispatchStride = 16u;

    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowIndirectPassData data) =>
        _ = access.Read(
            data.Arguments,
            new BufferRange(data.Offset, DispatchStride),
            PipelineSync.ExecuteIndirect,
            ResourceAccess.IndirectArgument);

    internal static void Record(
        ref ComputePassCommandScope commands,
        in VirtualShadowIndirectPassData data)
    {
        Buffer arguments = commands.GetBuffer(data.Arguments);
        commands.ExecuteIndirect(
            data.Layout,
            new BufferRegion(arguments, new BufferRange(data.Offset, DispatchStride)),
            1);
    }
}

internal readonly record struct VirtualShadowPoolResetPassData(
    GraphBufferId FreePageCount,
    GraphBufferId EvictionBucketCounts)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowPoolResetPassData data)
    {
        _ = access.Write(
            data.FreePageCount,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
        _ = access.Write(
            data.EvictionBucketCounts,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowPoolResetPassData data)
    {
        Buffer free = commands.GetBuffer(data.FreePageCount);
        commands.ClearBuffer(free, new BufferRange(0, free.Info.Size));
        Buffer buckets = commands.GetBuffer(data.EvictionBucketCounts);
        commands.ClearBuffer(buckets, new BufferRange(0, buckets.Info.Size));
    }
}

internal readonly record struct VirtualShadowFrameResetPassData(
    GraphBufferId ActivePageCount,
    GraphBufferId RasterRegionGrid,
    GraphBufferId ClusterInvocationDispatchArguments,
    GraphBufferId PageWorkDispatchArguments,
    GraphBufferId AllocationFailures)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowFrameResetPassData data)
    {
        Write(ref access, data.ActivePageCount);
        Write(ref access, data.RasterRegionGrid);
        Write(ref access, data.ClusterInvocationDispatchArguments);
        Write(ref access, data.PageWorkDispatchArguments);
        Write(ref access, data.AllocationFailures);
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowFrameResetPassData data)
    {
        Clear(ref commands, data.ActivePageCount);
        Clear(ref commands, data.RasterRegionGrid);
        Clear(ref commands, data.ClusterInvocationDispatchArguments);
        Clear(ref commands, data.PageWorkDispatchArguments);
        Clear(ref commands, data.AllocationFailures);
    }

    private static void Write(ref PassDefinition access, GraphBufferId buffer) =>
        _ = access.Write(
            buffer,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    private static void Clear(ref CopyPassCommandScope commands, GraphBufferId buffer)
    {
        Buffer resource = commands.GetBuffer(buffer);
        commands.ClearBuffer(resource, new BufferRange(0, resource.Info.Size));
    }
}

internal readonly record struct VirtualShadowBufferClearPassData(GraphBufferInput Buffer)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowBufferClearPassData data)
        => _ = access.Write(
            data.Buffer,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowBufferClearPassData data)
    {
        BufferRegion buffer = commands.GetBufferRegion(data.Buffer);
        commands.ClearBuffer(buffer.Buffer, buffer.Range);
    }
}

internal readonly record struct VirtualShadowPageTableCopyData(
    GraphBufferInput Source,
    GraphBufferInput Destination,
    ulong ByteCount,
    bool HasHistory)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowPageTableCopyData data)
    {
        if (data.HasHistory)
        {
            _ = access.Read(
                data.Source,
                BufferRange.Whole,
                PipelineSync.Copy,
                ResourceAccess.CopySource);
        }
        _ = access.Write(
            data.Destination,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
    }

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowPageTableCopyData data)
    {
        BufferRegion destination = commands.GetBufferRegion(data.Destination);
        if (!data.HasHistory)
        {
            commands.ClearBuffer(destination.Buffer, new BufferRange(destination.Range.Offset, data.ByteCount));
            return;
        }
        BufferRegion source = commands.GetBufferRegion(data.Source);
        commands.CopyBuffer(new BufferCopy(
            source.Buffer,
            source.Range.Offset,
            destination.Buffer,
            destination.Range.Offset,
            data.ByteCount));
    }
}
