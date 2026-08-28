using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// One non-bindless compute command inside a graph-stage batch. Each command keeps its own
/// pipeline and parameter layout; the graph owns synchronization for the flattened binding list.
/// </summary>
internal readonly record struct ClusterComputeCommand(
    LinkedComputeKernel Kernel,
    int BindingOffset,
    int BindingCount,
    GraphBufferId Arguments,
    ulong ArgumentOffset);

/// <summary>
/// Records a material/bin command sequence as one graph pass and several indirect commands.
/// This preserves the intentionally non-bindless material model while removing per-material
/// graph analysis, callback, barrier and command-context overhead.
/// </summary>
internal sealed class ClusterComputeCommandBatch
{
    internal ClusterComputeCommandBatch(
        IndirectCommandLayout layout,
        GraphParameterResourceBinding[] bindings,
        ClusterComputeCommand[] commands)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    internal IndirectCommandLayout Layout { get; }
    internal GraphParameterResourceBinding[] Bindings { get; }
    internal ClusterComputeCommand[] Commands { get; }

    internal static void Declare(
        ref PassDefinition definition,
        ref ClusterComputeCommandBatch batch)
    {
        definition.Bind(batch.Bindings);
        foreach (ClusterComputeCommand command in batch.Commands)
        {
            _ = definition.Read(
                command.Arguments,
                new BufferRange(command.ArgumentOffset, ClusterIndirectAbi.DispatchBytes),
                PipelineSync.ExecuteIndirect,
                ResourceAccess.IndirectArgument);
        }
    }

    internal static void Record(
        ref ComputePassCommandScope commandScope,
        in ClusterComputeCommandBatch batch)
    {
        ReadOnlySpan<ResourceBinding> resolved =
            commandScope.GetResolvedParameterBindings();
        foreach (ClusterComputeCommand command in batch.Commands)
        {
            commandScope.SetPipeline(command.Kernel.Pipeline);
            ReadOnlySpan<ResourceBinding> commandBindings = resolved.Slice(
                command.BindingOffset,
                command.BindingCount);
            commandScope.SetTransientParameterBindings(new ParameterBlockBindings(
                command.Kernel.Program.ParameterLayout,
                commandBindings,
                default));
            Buffer arguments = commandScope.GetBuffer(command.Arguments);
            commandScope.ExecuteIndirect(
                batch.Layout,
                new BufferRegion(
                    arguments,
                    new BufferRange(
                        command.ArgumentOffset,
                        ClusterIndirectAbi.DispatchBytes)),
                1);
        }
    }
}

internal readonly record struct ClusterRasterCommand(
    LinkedRasterPipeline Pipeline,
    int BindingOffset,
    int BindingCount,
    GraphBufferId Arguments,
    ulong ArgumentOffset);

/// <summary>
/// One hardware-raster graph stage containing the non-bindless draw commands for every material
/// bin. The render targets and rendering scope are opened once for the complete command sequence.
/// </summary>
internal sealed class ClusterRasterCommandBatch
{
    internal ClusterRasterCommandBatch(
        IndirectCommandLayout layout,
        GraphParameterResourceBinding[] bindings,
        ClusterRasterCommand[] commands,
        GraphColorAttachmentViewId visibility,
        GraphDepthStencilViewId depth,
        int width,
        int height)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Visibility = visibility;
        Depth = depth;
        Width = width;
        Height = height;
    }

    internal IndirectCommandLayout Layout { get; }
    internal GraphParameterResourceBinding[] Bindings { get; }
    internal ClusterRasterCommand[] Commands { get; }
    internal GraphColorAttachmentViewId Visibility { get; }
    internal GraphDepthStencilViewId Depth { get; }
    internal int Width { get; }
    internal int Height { get; }

    internal static void Declare(
        ref PassDefinition definition,
        ref ClusterRasterCommandBatch batch)
    {
        definition.Bind(batch.Bindings);
        PassRenderingRegionId region = definition.DefineRenderingRegion(
            0,
            0,
            checked((uint)batch.Width),
            checked((uint)batch.Height));
        definition.ColorAttachment(
            region,
            0,
            batch.Visibility,
            LoadType.Load,
            StoreType.Store,
            WriteCoverage.Partial,
            default);
        definition.DepthStencilAttachment(
            region,
            batch.Depth,
            LoadType.Load,
            StoreType.Store,
            WriteCoverage.Partial,
            1f,
            LoadType.Discard,
            StoreType.Discard,
            WriteCoverage.Complete,
            0);
        foreach (ClusterRasterCommand command in batch.Commands)
        {
            _ = definition.Read(
                command.Arguments,
                new BufferRange(command.ArgumentOffset, ClusterIndirectAbi.DrawBytes),
                PipelineSync.ExecuteIndirect,
                ResourceAccess.IndirectArgument);
        }
    }

    internal static void Record(
        ref GeneralPassCommandScope commandScope,
        in ClusterRasterCommandBatch batch)
    {
        commandScope.BeginRendering();
        try
        {
            commandScope.SetViewports([new Viewport(0, 0, batch.Width, batch.Height)]);
            commandScope.SetScissors([new ScissorRect(0, 0, batch.Width, batch.Height)]);
            ReadOnlySpan<ResourceBinding> resolved =
                commandScope.GetResolvedParameterBindings();
            foreach (ClusterRasterCommand command in batch.Commands)
            {
                commandScope.SetPipeline(command.Pipeline.Pipeline);
                commandScope.SetTransientParameterBindings(new ParameterBlockBindings(
                    command.Pipeline.Program.ParameterLayout,
                    resolved.Slice(command.BindingOffset, command.BindingCount),
                    default));
                Buffer arguments = commandScope.GetBuffer(command.Arguments);
                commandScope.ExecuteIndirect(
                    batch.Layout,
                    new BufferRegion(
                        arguments,
                        new BufferRange(
                            command.ArgumentOffset,
                            ClusterIndirectAbi.DrawBytes)),
                    1);
            }
        }
        finally
        {
            commandScope.EndRendering();
        }
    }
}
