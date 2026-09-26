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

internal readonly record struct VirtualShadowReceiverMaskResetPassData(
    GraphBufferId ReceiverMasks)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowReceiverMaskResetPassData data) =>
        _ = access.Write(
            data.ReceiverMasks,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowReceiverMaskResetPassData data)
    {
        Buffer buffer = commands.GetBuffer(data.ReceiverMasks);
        commands.ClearBuffer(buffer, new BufferRange(0, buffer.Info.Size));
    }
}

internal readonly record struct VirtualShadowBufferResetPassData(GraphBufferId Buffer)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowBufferResetPassData data) =>
        _ = access.Write(
            data.Buffer,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowBufferResetPassData data)
    {
        Buffer buffer = commands.GetBuffer(data.Buffer);
        commands.ClearBuffer(buffer, new BufferRange(0, buffer.Info.Size));
    }
}

internal readonly record struct VirtualShadowInputBufferResetPassData(GraphBufferInput Buffer)
{
    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowInputBufferResetPassData data) =>
        _ = access.Write(
            data.Buffer,
            BufferRange.Whole,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowInputBufferResetPassData data)
    {
        BufferRegion region = commands.GetBufferRegion(data.Buffer);
        commands.ClearBuffer(region.Buffer, region.Range);
    }
}
