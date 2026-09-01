using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Installs the command-recording behavior used to execute already generated Cluster raster work.
/// The selected callbacks are captured by retained graph passes during pipeline construction; frame
/// recording performs no executor lookup.
/// </summary>
public sealed class ClusterRasterExecutor
{
    /// <summary>Executes software dispatches and hardware draws from generated indirect work.</summary>
    public static ClusterRasterExecutor Indirect { get; } = new(
        ClusterSoftwareRasterExecution.RecordIndirect,
        ClusterHardwareRasterExecution.RecordIndirect);

    public ClusterRasterExecutor(
        ComputeFrameCallback<ClusterSoftwareRasterExecution> software,
        RasterFrameCallback<ClusterHardwareRasterExecution> hardware)
    {
        Software = software ?? throw new ArgumentNullException(nameof(software));
        Hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
    }

    internal ComputeFrameCallback<ClusterSoftwareRasterExecution> Software { get; }

    internal RasterFrameCallback<ClusterHardwareRasterExecution> Hardware { get; }
}

/// <summary>Resources and command range for one generated software-raster work bin.</summary>
public readonly record struct ClusterSoftwareRasterExecution(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset)
{
    public static void RecordIndirect(
        ref ComputePassCommandScope commands,
        in ClusterSoftwareRasterExecution execution)
    {
        Buffer arguments = commands.GetBuffer(execution.Arguments);
        commands.ExecuteIndirect(
            execution.Layout,
            new BufferRegion(
                arguments,
                new BufferRange(execution.Offset, ClusterIndirectAbi.DispatchBytes)),
            1);
    }
}

/// <summary>Resources, command range, and render extent for one generated hardware-raster work bin.</summary>
public readonly record struct ClusterHardwareRasterExecution(
    IndirectCommandLayout Layout,
    GraphBufferId Arguments,
    ulong Offset,
    int Width,
    int Height)
{
    public static void RecordIndirect(
        ref RasterPassCommandScope commands,
        in ClusterHardwareRasterExecution execution)
    {
        commands.SetViewports(
            [new Viewport(0, 0, execution.Width, execution.Height)]);
        commands.SetScissors(
            [new ScissorRect(0, 0, execution.Width, execution.Height)]);
        Buffer arguments = commands.GetBuffer(execution.Arguments);
        commands.ExecuteIndirect(
            execution.Layout,
            new BufferRegion(
                arguments,
                new BufferRange(execution.Offset, ClusterIndirectAbi.DrawBytes)),
            1);
    }
}
