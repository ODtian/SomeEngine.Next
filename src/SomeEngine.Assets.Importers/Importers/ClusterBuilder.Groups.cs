using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static List<int[]> PartitionClusterGroups(Vector3[] positions, List<BuilderMeshlet> clusters,
        List<uint> indices, List<int> current)
    {
        if (current.Count <= 32) return [Enumerable.Range(0, current.Count).ToArray()];
        var graph = new ClusterGraph(current.Count);
        var edges = new Dictionary<(Vector3 First, Vector3 Second), List<int>>();
        var sharedEdges = new Dictionary<(int First, int Second), int>();
        for (int index = 0; index < current.Count; index++)
        {
            BuilderMeshlet cluster = clusters[current[index]];
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(indices).Slice(cluster.IndicesOffset, cluster.IndicesCount);
            for (int corner = 0; corner < triangles.Length; corner++)
            {
                Vector3 first = positions[triangles[corner]];
                minimum = Vector3.Min(minimum, first); maximum = Vector3.Max(maximum, first);
            }
            ClusterGraph.AddTriangleEdges(edges, triangles, positions, index, neighbor =>
            {
                if (neighbor == index) return;
                var pair = (neighbor, index);
                sharedEdges[pair] = sharedEdges.GetValueOrDefault(pair) + 1;
            });
            graph.Include(minimum); graph.Include(maximum);
            graph.Centers[index] = (minimum + maximum) * .5f;
        }
        foreach (var pair in sharedEdges)
        {
            BuilderMeshlet a = clusters[current[pair.Key.First]], b = clusters[current[pair.Key.Second]];
            bool siblings = a.Level != 0 && a.GroupId == b.GroupId;
            graph.Connect(pair.Key.First, pair.Key.Second, pair.Value * (siblings ? 12 : 16) + 4);
        }
        graph.ConnectNearbyComponents();
        return graph.Partition(8, 32);
    }

    private static byte[] LockGroupBoundaries(Vector3[] positions, int vertexCount, List<BuilderMeshlet> clusters,
        List<uint> indices, List<int> current, List<int[]> groups)
    {
        int[] clusterGroups = new int[current.Count];
        for (int group = 0; group < groups.Count; group++)
            foreach (int cluster in groups[group]) clusterGroups[cluster] = group;
        var edges = new Dictionary<(Vector3 First, Vector3 Second), List<int>>();
        for (int cluster = 0; cluster < current.Count; cluster++)
        {
            BuilderMeshlet data = clusters[current[cluster]];
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(indices).Slice(data.IndicesOffset, data.IndicesCount);
            ClusterGraph.AddTriangleEdges(edges, triangles, positions, clusterGroups[cluster]);
        }
        var locked = new HashSet<Vector3>();
        foreach (var pair in edges)
            if (edges.TryGetValue((pair.Key.Second, pair.Key.First), out List<int>? opposite) &&
                pair.Value.Any(group => opposite.Any(other => other != group)))
            {
                locked.Add(pair.Key.First); locked.Add(pair.Key.Second);
            }
        var result = new byte[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++) result[vertex] = locked.Contains(positions[vertex]) ? (byte)1 : (byte)0;
        return result;
    }
}
