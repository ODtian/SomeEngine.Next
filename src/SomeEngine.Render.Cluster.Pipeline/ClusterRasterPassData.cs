using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterSoftwareRasterPassData(
    IndirectCommandLayout IndirectLayout,
    GraphBufferId IndirectArguments,
    ulong IndirectOffset)
{
    internal static void Declare(
        ref PassDefinition pass,
        ref ClusterSoftwareRasterPassData data)
    {
        _ = pass.Read(
            data.IndirectArguments,
            new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DispatchBytes),
            PipelineSync.ExecuteIndirect,
            ResourceAccess.IndirectArgument);
    }

    internal static void Record(
        ref ComputePassCommandScope commands,
        in ClusterSoftwareRasterPassData data)
    {
        Buffer arguments = commands.GetBuffer(data.IndirectArguments);
        commands.ExecuteIndirect(
            data.IndirectLayout,
            new BufferRegion(
                arguments,
                new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DispatchBytes)),
            1);
    }
}

internal readonly record struct ClusterHardwareRasterPassData(
    IndirectCommandLayout IndirectLayout,
    GraphBufferId IndirectArguments,
    ulong IndirectOffset,
    int Width,
    int Height)
{
    internal static void Declare(
        ref PassDefinition pass,
        ref ClusterHardwareRasterPassData data)
    {
        _ = pass.Read(
            data.IndirectArguments,
            new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DrawBytes),
            PipelineSync.ExecuteIndirect,
            ResourceAccess.IndirectArgument);
    }

    internal static void Record(
        ref RasterPassCommandScope commands,
        in ClusterHardwareRasterPassData data)
    {
        commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
        commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
        Buffer arguments = commands.GetBuffer(data.IndirectArguments);
        commands.ExecuteIndirect(
            data.IndirectLayout,
            new BufferRegion(
                arguments,
                new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DrawBytes)),
            1);
    }
}
