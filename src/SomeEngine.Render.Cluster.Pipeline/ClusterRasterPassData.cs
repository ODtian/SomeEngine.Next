using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterHardwareRasterPassData(
    IndirectCommandLayout IndirectLayout,
    GraphBufferId IndirectArguments,
    ulong IndirectOffset,
    int Width,
    int Height,
    bool UsesMesh)
{
    internal static void Declare(
        ref PassDefinition pass,
        ref ClusterHardwareRasterPassData data)
    {
        _ = pass.Read(
            data.IndirectArguments,
            new BufferRange(data.IndirectOffset + (data.UsesMesh ? 16u : 0u),
                data.UsesMesh ? ClusterIndirectAbi.DispatchBytes : ClusterIndirectAbi.DrawBytes),
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
        if (data.UsesMesh)
        {
            commands.DispatchMeshIndirect(new BufferRegion(arguments,
                new BufferRange(data.IndirectOffset + 16u, ClusterIndirectAbi.DispatchBytes)));
            return;
        }
        commands.ExecuteIndirect(
            data.IndirectLayout,
            new BufferRegion(
                arguments,
                new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DrawBytes)),
            1);
    }
}
