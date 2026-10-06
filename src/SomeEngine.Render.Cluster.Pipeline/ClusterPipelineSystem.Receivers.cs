using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private GraphRegionId _surfaceReceiverRegion;

    /// <summary>Places user receiver evaluation between primary visibility and light-dependent shading.</summary>
    public void ConfigureSurfaceReceivers(GraphRegionId region)
    {
        if (_graphInstalled) throw new InvalidOperationException("Surface receiver stages must be configured before graph installation.");
        _surfaceReceiverRegion = region;
    }

    private readonly record struct SurfaceReceiverReset(GraphBufferId Receivers)
    {
        public static void Declare(ref PassDefinition pass, ref SurfaceReceiverReset data) =>
            _ = pass.Write(data.Receivers, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        public static void Record(ref CopyPassCommandScope commands, in SurfaceReceiverReset data)
        {
            var receivers = commands.GetBuffer(data.Receivers);
            commands.ClearBuffer(receivers, new BufferRange(0, receivers.Info.Size));
        }
    }
}
