using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Features.Shadows;

internal readonly record struct VirtualShadowViewCopyPassData(GraphBufferSlice Source, GraphBufferId Destination)
{
    internal static void Declare(ref PassDefinition pass, ref VirtualShadowViewCopyPassData data)
    {
        _ = pass.Read(data.Source.Buffer, data.Source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
        _ = pass.Write(data.Destination, new BufferRange(0, data.Source.Range.Size), PipelineSync.Copy,
            ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }

    internal static void Record(ref CopyPassCommandScope commands, in VirtualShadowViewCopyPassData data) =>
        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
            commands.GetBuffer(data.Destination), 0, data.Source.Range.Size));
}

internal readonly record struct VirtualShadowResolutionCopyPassData(GraphBufferId Source, GraphBufferId Destination)
{
    private static readonly BufferRange BiasRange = new(0, sizeof(float));

    internal static void Declare(ref PassDefinition pass, ref VirtualShadowResolutionCopyPassData data)
    {
        _ = pass.Read(data.Source, BiasRange, PipelineSync.Copy, ResourceAccess.CopySource);
        _ = pass.Write(data.Destination, BiasRange, PipelineSync.Copy, ResourceAccess.CopyDestination,
            WriteCoverage.Complete);
    }

    internal static void Record(ref CopyPassCommandScope commands, in VirtualShadowResolutionCopyPassData data) =>
        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
            commands.GetBuffer(data.Destination), 0, BiasRange.Size));
}

internal readonly record struct VirtualShadowAtlasInitializePassData(GraphTextureId Atlas, uint ArrayLayers = 1)
{
    private TextureSubresourceRange AtlasRange =>
        new(0, 1, 0, ArrayLayers, TextureAspects.Color);

    internal static void Declare(
        ref PassDefinition access,
        ref VirtualShadowAtlasInitializePassData data) =>
        _ = access.Write(
            data.Atlas,
            data.AtlasRange,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            TextureLayout.CopyDestination,
            WriteCoverage.Complete);

    internal static void Record(
        ref CopyPassCommandScope commands,
        in VirtualShadowAtlasInitializePassData data) =>
        commands.ClearTexture(
            commands.GetTexture(data.Atlas),
            data.AtlasRange,
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
