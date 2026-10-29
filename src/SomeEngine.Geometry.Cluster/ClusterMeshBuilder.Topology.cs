using System.Runtime.InteropServices;

namespace SomeEngine.Geometry.Cluster;

public static partial class ClusterMeshBuilder
{
    private static void BuildLocalTopology(List<ClusterMeshlet> clusters, List<uint> indices)
    {
        // Clustering establishes vertex capacity; page packing establishes wave batches.
        for (int index = 0; index < clusters.Count; index++)
        {
            ClusterMeshlet cluster = clusters[index];
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(indices)
                .Slice(cluster.IndicesOffset, cluster.IndicesCount);
            var vertexIndices = new Dictionary<uint, byte>();
            var vertices = new List<uint>(cluster.VertexCount);
            var localIndices = new byte[triangles.Length];
            for (int corner = 0; corner < triangles.Length; corner++)
            {
                uint source = triangles[corner];
                if (!vertexIndices.TryGetValue(source, out byte local))
                {
                    local = (byte)vertices.Count;
                    vertexIndices.Add(source, local);
                    vertices.Add(source);
                }
                localIndices[corner] = local;
            }
            cluster.VertexCount = vertices.Count;
            cluster.LocalVertices = vertices.ToArray();
            cluster.LocalIndices = localIndices;
            clusters[index] = cluster;
        }
    }
}
