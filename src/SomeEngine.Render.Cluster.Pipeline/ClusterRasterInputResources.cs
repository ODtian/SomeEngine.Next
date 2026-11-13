namespace SomeEngine.Render.Cluster.Pipeline;

internal static class ClusterRasterInputResources
{
    internal static bool Contains(string name) => name is "RasterSpillClusters" or "RasterInputBinIndices"
        or "RasterInputDrawArgs" or "RasterInputReadOffset0" or "RasterInputReadOffset1" or "RasterInputReadOffset2";
}
