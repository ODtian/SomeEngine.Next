using System.Runtime.InteropServices;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

public sealed partial class ClusterRenderResources
{
    private ulong _geometryBuildFrame;
    private readonly List<GraphPassId> _geometryBuilds = [];

    internal ClusterGeometry ReserveGpu(in RenderPrepareSystemContext context, ClusterGpuBufferSizes sizes,
        string encoding, ulong revision, Bounds? bounds)
    {
        using RenderTimelineLease access = context.ActiveScope.AcquireTimeline(_residencyTimeline);
        return _residency.ReserveGpu(sizes, encoding, revision, bounds);
    }

    internal void ReleaseGpu(in RenderPrepareSystemContext context, ClusterGeometry geometry)
    {
        using RenderTimelineLease access = context.ActiveScope.AcquireTimeline(_residencyTimeline);
        _residency.ReleaseGpu(geometry);
    }

    internal ClusterGpuAllocation GetGpu(ClusterGeometry geometry) => _residency.GetGpu(geometry);
    internal ClusterResidencyBinding GpuBuffers => _residency.GetBinding();

    internal RenderFrameUseLease UseGpu(RenderFrame frame)
    {
        RenderFrameUseLease use = frame.AcquireUse([_residencyTimeline]);
        use.RegisterGeneration(_residencyTimeline, 0);
        return use;
    }

    internal void AddGeometryBuild(ulong frame, GraphPassId pass)
    {
        if (_geometryBuildFrame != frame) { _geometryBuilds.Clear(); _geometryBuildFrame = frame; }
        _geometryBuilds.Add(pass);
    }

    internal ReadOnlySpan<GraphPassId> GeometryBuilds(ulong frame) =>
        _geometryBuildFrame == frame ? CollectionsMarshal.AsSpan(_geometryBuilds) : [];
}
