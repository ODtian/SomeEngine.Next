using SomeEngine.Graphics;
using SomeEngine.Render.Assets;

namespace SomeEngine.Render.Cluster.Pipeline;

internal static class ClusterRasterPolicy
{
    internal static bool UsesMaterialRanges(LinkedRasterPipeline pipeline) =>
        pipeline.GeometryEntry is "MSVisBufferMeshlet" or ClusterMaterialEntries.HardwareMeshlet;

    internal static bool SupportsMeshlets(IGraphicsBackend backend, Device device) =>
        SupportsMesh(backend, device) &&
        backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh!.MaximumOutputVertices >= 64 &&
        mesh.MaximumOutputPrimitives >= 128;

    internal static bool SupportsMesh(IGraphicsBackend backend, Device device) =>
        backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh is not null &&
        mesh.IndirectDispatch &&
        mesh.MaximumThreadsPerGroup >= 32 &&
        mesh.MaximumOutputVertices >= 96 &&
        mesh.MaximumOutputPrimitives >= 64;
}
