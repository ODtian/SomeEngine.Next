using System.Runtime.InteropServices;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

public sealed partial class ClusterRenderResources
{
    internal ClusterGeometry ReserveGpu(in RenderPrepareSystemContext context, ClusterGpuBufferSizes sizes,
        string encoding, ulong revision, Bounds? bounds) => _residency.ReserveGpu(sizes, encoding, revision, bounds);
    internal void ReleaseGpu(in RenderPrepareSystemContext context, ClusterGeometry geometry) => _residency.ReleaseGpu(geometry);
    internal ClusterGpuAllocation GetGpu(ClusterGeometry geometry) => _residency.GetGpu(geometry);
    internal ClusterGeometryBuffers GeometryBuffers => _residency.GeometryBuffers;

    private sealed class GeometryBuildData { internal readonly List<GraphPassId> Builds = []; }
    internal void AddGeometryBuild(ref RenderGraphFrame frame, GraphPassId pass) =>
        frame.GetFrameData(_residency, static () => new GeometryBuildData()).Builds.Add(pass);
    internal ReadOnlySpan<GraphPassId> GeometryBuilds(ref RenderGraphFrame frame) =>
        CollectionsMarshal.AsSpan(frame.GetFrameData(_residency, static () => new GeometryBuildData()).Builds);
}
